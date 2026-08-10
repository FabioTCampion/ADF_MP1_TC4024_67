export type PageId = "dashboard" | "metrics" | "weights" | "breaks" | "status" | "graphs" | "alarms" | "commands" | "history" | "users" | "updates";

export type NavigationItem = {
  id: PageId;
  label: string;
  icon: "dashboard" | "metrics" | "weights" | "breaks" | "status" | "graphs" | "alarm" | "command" | "history" | "users" | "updates";
};

type Props = {
  currentPage: PageId;
  items: readonly NavigationItem[];
  collapsed: boolean;
  online: boolean;
  onSelect: (page: PageId) => void;
  onToggle: () => void;
};

function Icon({ name }: { name: NavigationItem["icon"] }) {
  if (name === "dashboard") {
    return <svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="7" /><rect x="14" y="3" width="7" height="7" /><rect x="3" y="14" width="7" height="7" /><rect x="14" y="14" width="7" height="7" /></svg>;
  }
  if (name === "status") {
    return <svg viewBox="0 0 24 24"><path d="M4 18V8l8-4 8 4v10M3 20h18" /><path d="M8 16v-4h8v4" /></svg>;
  }
  if (name === "graphs") {
    return <svg viewBox="0 0 24 24"><path d="M4 19V5M4 19h16M7 15l4-4 3 2 5-6" /></svg>;
  }
  if (name === "metrics") {
    return <svg viewBox="0 0 24 24"><path d="M4 19V9M10 19V5M16 19v-7M22 19H2" /><path d="m4 7 6-4 6 6 5-4" /></svg>;
  }
  if (name === "weights") {
    return <svg viewBox="0 0 24 24"><path d="M6 20h12l2-12H4l2 12Z" /><path d="M8 8a4 4 0 0 1 8 0M12 11v4M9 15h6" /></svg>;
  }
  if (name === "breaks") {
    return <svg viewBox="0 0 24 24"><path d="M4 19V5M4 19h16M6 15l4-4 3 2 2-6 4 3" /><path d="m12 3 2 3-3 2 3 3-2 3" /></svg>;
  }
  if (name === "alarm") {
    return <svg viewBox="0 0 24 24"><path d="M18 9a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M9.8 21h4.4" /></svg>;
  }
  if (name === "command") {
    return <svg viewBox="0 0 24 24"><path d="M12 2v8M7 5.5A8 8 0 1 0 17 5.5" /><path d="M8 14h8M8 18h5" /></svg>;
  }
  if (name === "users") {
    return <svg viewBox="0 0 24 24"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M22 21v-2a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8" /></svg>;
  }
  if (name === "updates") {
    return <svg viewBox="0 0 24 24"><path d="M12 3v12M7 10l5 5 5-5" /><path d="M5 20h14" /><path d="M19 8a7 7 0 0 0-13.8-1.5" /></svg>;
  }
  return <svg viewBox="0 0 24 24"><path d="M4 12a8 8 0 1 0 2.3-5.7L4 8.6" /><path d="M4 4v4.6h4.6M12 8v5l3 2" /></svg>;
}

export default function SideMenu({
  currentPage,
  items,
  collapsed,
  online,
  onSelect,
  onToggle,
}: Props) {
  const toggleLabel = collapsed ? "Expandir menu" : "Recolher menu";

  return (
    <aside
      className={`side-menu${collapsed ? " side-menu--collapsed" : ""}`}
      aria-label="Navegação principal"
    >
      <div className="side-menu-heading">
        <div className="side-brand">
          <img src="/brand/cpnteck-icon-white.png" alt="CPNTeck - Automação Industrial" />
          <div>
            <b>Paper Machine</b>
            <span>Historian</span>
          </div>
        </div>
        <button
          type="button"
          className="menu-toggle"
          onClick={onToggle}
          aria-label={toggleLabel}
          aria-expanded={!collapsed}
          title={toggleLabel}
        >
          <svg viewBox="0 0 24 24"><path d="m14 7-5 5 5 5" /></svg>
        </button>
      </div>

      <nav aria-label="Telas da aplicação">
        {items.map((item) => (
          <button
            type="button"
            key={item.id}
            className={currentPage === item.id ? "active" : ""}
            aria-current={currentPage === item.id ? "page" : undefined}
            aria-label={item.label}
            onClick={() => onSelect(item.id)}
            title={collapsed ? item.label : undefined}
            data-tooltip={collapsed ? item.label : undefined}
          >
            <i><Icon name={item.icon} /></i>
            <span>{item.label}</span>
          </button>
        ))}
      </nav>

      <div className="side-footer" title={`PLC: ${online ? "ADS conectado" : "ADS desconectado"}`}>
        <img className="side-logo" src="/brand/cpnteck-logo.svg" alt="CPNTeck - Automação Industrial" />
        <span className={`side-status${online ? " online" : ""}`}>
          <i />
          <span>{online ? "ADS conectado" : "ADS desconectado"}</span>
        </span>
        <span className="runtime-label">PLC Runtime 1 · 851</span>
      </div>
    </aside>
  );
}
