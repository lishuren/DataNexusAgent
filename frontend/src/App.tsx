import { Routes, Route } from "react-router-dom";
import { Layout } from "@/components/Layout";
import { ErrorBoundary } from "@/components/ErrorBoundary";
import ProcessPage from "@/pages/ProcessPage";
import AgentsPage from "@/pages/AgentsPage";
import SkillsPage from "@/pages/SkillsPage";
import MarketplacePage from "@/pages/MarketplacePage";

export default function App() {
  return (
    <ErrorBoundary>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<ProcessPage />} />
          <Route path="agents" element={<AgentsPage />} />
          <Route path="skills" element={<SkillsPage />} />
          <Route path="marketplace" element={<MarketplacePage />} />
        </Route>
      </Routes>
    </ErrorBoundary>
  );
}
