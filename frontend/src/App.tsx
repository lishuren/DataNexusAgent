import { Routes, Route } from "react-router-dom";
import { Layout } from "@/components/Layout";
import ProcessPage from "@/pages/ProcessPage";
import AgentsPage from "@/pages/AgentsPage";
import SkillsPage from "@/pages/SkillsPage";
import MarketplacePage from "@/pages/MarketplacePage";

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<ProcessPage />} />
        <Route path="agents" element={<AgentsPage />} />
        <Route path="skills" element={<SkillsPage />} />
        <Route path="marketplace" element={<MarketplacePage />} />
      </Route>
    </Routes>
  );
}
