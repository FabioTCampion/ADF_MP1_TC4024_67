export type PageId = "dashboard" | "status" | "alarms" | "commands" | "history";

export type NavigationItem = {
  id: PageId;
  label: string;
  eyebrow: string;
  icon: "dashboard" | "status" | "alarm" | "command" | "history";
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
  if (name === "alarm") {
    return <svg viewBox="0 0 24 24"><path d="M18 9a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M9.8 21h4.4" /></svg>;
  }
  if (name === "command") {
    return <svg viewBox="0 0 24 24"><path d="M12 2v8M7 5.5A8 8 0 1 0 17 5.5" /><path d="M8 14h8M8 18h5" /></svg>;
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
  return (
    <aside className={`side-menu${collapsed ? " side-menu--collapsed" : ""}`}>
      <div className="side-brand">
        <img src="/brand/cpnteck-icon-white.png" alt="" />
        <div>
          <b>Paper Machine</b>
          <span>Historian</span>
        </div>
      </div>

      <button
        type="button"
        className="menu-toggle"
        onClick={onToggle}
        aria-label={collapsed ? "Expandir menu" : "Recolher menu"}
      >
        <svg viewBox="0 0 24 24"><path d="m15 18-6-6 6-6" /></svg>
      </button>

      <nav aria-label="Navegação principal">
        {items.map((item) => (
          <button
            type="button"
            key={item.id}
            className={currentPage === item.id ? "active" : ""}
            onClick={() => onSelect(item.id)}
            title={collapsed ? item.label : undefined}
          >
            <i><Icon name={item.icon} /></i>
            <span>
              <small>{item.eyebrow}</small>
              {item.label}
            </span>
          </button>
        ))}
      </nav>

      <div className="side-status">
        <span className={`status-dot${online ? " online" : ""}`} />
        <div>
          <b>{online ? "ADS conectado" : "ADS desconectado"}</b>
          <span>PLC Runtime 1 · 851</span>
        </div>
      </div>

      <img className="side-logo" src="/brand/cpnteck-logo-white.png" alt="CPNTeck" />
    </aside>
  );
}
