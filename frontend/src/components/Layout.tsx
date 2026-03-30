import { useEffect, useState } from "react";
import { NavLink, Outlet } from "react-router-dom";
import { loadDisplayName, logout } from "@/services/auth";

export function Layout() {
  const [displayName, setDisplayName] = useState<string | undefined>();

  useEffect(() => {
    loadDisplayName().then(setDisplayName);
  }, []);
  return (
    <div className="app">
      <header>
        <h1>
          <span style={{ color: "var(--primary)" }}>⬡</span> DataNexus
        </h1>
        <nav>
          <NavLink to="/">Execute</NavLink>
          <NavLink to="/agents">Agents</NavLink>
          <NavLink to="/skills">Skills</NavLink>
          <NavLink to="/orchestrations">Orchestrations</NavLink>
          <NavLink to="/marketplace">Marketplace</NavLink>
          <span className="user-info">
            <span className="status-dot" />
            {displayName}
          </span>
          <button className="btn btn-sm btn-danger" onClick={logout}>
            Sign out
          </button>
        </nav>
      </header>
      <main>
        <Outlet />
      </main>
    </div>
  );
}
