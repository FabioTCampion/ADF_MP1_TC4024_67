import {
  createContext,
  useContext,
  useEffect,
  useState,
  type FormEvent,
  type ReactNode,
} from "react";

export type AuthUser = {
  id: number;
  userName: string;
  displayName: string;
  role: "Viewer" | "Administrator";
  permissions: string[];
};

type AuthContextValue = {
  user: AuthUser;
  logout: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("AuthContext indisponível.");
  return context;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [requiresSetup, setRequiresSetup] = useState(false);

  useEffect(() => {
    Promise.all([fetch("/api/auth/status"), fetch("/api/auth/me")])
      .then(async ([statusResponse, meResponse]) => {
        if (statusResponse.ok) {
          const status = (await statusResponse.json()) as { requiresSetup: boolean };
          setRequiresSetup(status.requiresSetup);
        }
        if (meResponse.ok) setUser((await meResponse.json()) as AuthUser);
      })
      .finally(() => setLoading(false));
  }, []);

  const logout = async () => {
    await fetch("/api/auth/logout", { method: "POST" });
    setUser(null);
  };

  if (loading) {
    return (
      <main className="auth-shell">
        <div className="auth-card auth-loading">Carregando aplicação…</div>
      </main>
    );
  }

  if (!user) {
    return (
      <LoginScreen
        requiresSetup={requiresSetup}
        onAuthenticated={(authenticatedUser) => {
          setUser(authenticatedUser);
          setRequiresSetup(false);
        }}
      />
    );
  }

  return <AuthContext.Provider value={{ user, logout }}>{children}</AuthContext.Provider>;
}

function LoginScreen({
  requiresSetup,
  onAuthenticated,
}: {
  requiresSetup: boolean;
  onAuthenticated: (user: AuthUser) => void;
}) {
  const [userName, setUserName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaving(true);
    setError("");

    try {
      const response = await fetch(
        requiresSetup ? "/api/auth/setup" : "/api/auth/login",
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(
            requiresSetup
              ? { userName, displayName, password }
              : { userName, password },
          ),
        },
      );

      if (!response.ok) {
        const result = (await response.json().catch(() => null)) as { error?: string } | null;
        throw new Error(
          response.status === 401
            ? "Usuário ou senha inválidos."
            : response.status === 429
              ? "Muitas tentativas. Aguarde um minuto."
              : result?.error ?? "Não foi possível autenticar.",
        );
      }

      onAuthenticated((await response.json()) as AuthUser);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Não foi possível autenticar.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <main className="auth-shell">
      <section className="auth-presentation">
        <div className="auth-machine-mark">PM</div>
        <p>Monitoramento industrial</p>
        <h2>Histórico confiável para decisões de processo.</h2>
        <span>Status, comandos e alarmes capturados diretamente do PLC.</span>
      </section>
      <form className="auth-card" onSubmit={submit}>
        <img src="/brand/cpnteck-logo.svg" alt="CPNTeck" />
        <div className="auth-heading">
          <p>Paper Machine Historian</p>
          <h1>{requiresSetup ? "Configuração inicial" : "Acessar o sistema"}</h1>
          <span>
            {requiresSetup
              ? "Crie o primeiro administrador local."
              : "Entre com suas credenciais para consultar os dados."}
          </span>
        </div>
        {requiresSetup && (
          <label>
            Nome completo
            <input
              autoFocus
              required
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              autoComplete="name"
            />
          </label>
        )}
        <label>
          Usuário
          <input
            autoFocus={!requiresSetup}
            required
            minLength={3}
            value={userName}
            onChange={(event) => setUserName(event.target.value)}
            autoComplete="username"
          />
        </label>
        <label>
          Senha
          <input
            required
            minLength={8}
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete={requiresSetup ? "new-password" : "current-password"}
          />
        </label>
        {error && <div className="auth-error">{error}</div>}
        <button className="primary-button" disabled={saving}>
          {saving ? "Aguarde…" : requiresSetup ? "Criar administrador" : "Entrar"}
        </button>
      </form>
    </main>
  );
}
