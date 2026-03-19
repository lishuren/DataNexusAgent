import type { UiField } from "@/types/api";

interface DynamicFormProps {
  fields: UiField[];
  values: Record<string, string>;
  onChange: (key: string, value: string) => void;
}

export function DynamicForm({ fields, values, onChange }: DynamicFormProps) {
  return (
    <div className="dynamic-form">
      <div className="form-grid">
        {fields.map((f) => {
          const fullClass = f.type === "textarea" || f.type === "file" ? " full" : "";
          return (
            <div key={f.key} className={`form-field${fullClass}`}>
              <label>
                {f.label}
                {f.required && <span style={{ color: "var(--danger)" }}> *</span>}
              </label>

              {f.type === "file" && (
                <div className="file-drop" onClick={(e) => {
                  const input = (e.currentTarget as HTMLElement).querySelector("input");
                  input?.click();
                }}>
                  📁 {f.placeholder ?? "Drop file here or click to browse"}
                  <input
                    type="file"
                    accept={f.accept}
                    style={{ display: "none" }}
                    onChange={(e) => {
                      const file = e.target.files?.[0];
                      if (file) onChange(f.key, file.name);
                    }}
                  />
                </div>
              )}

              {f.type === "textarea" && (
                <textarea
                  placeholder={f.placeholder}
                  value={values[f.key] ?? f.default ?? ""}
                  onChange={(e) => onChange(f.key, e.target.value)}
                />
              )}

              {f.type === "select" && (
                <select
                  value={values[f.key] ?? f.default ?? ""}
                  onChange={(e) => onChange(f.key, e.target.value)}
                >
                  {f.options?.map((opt) => (
                    <option key={opt} value={opt}>{opt}</option>
                  ))}
                </select>
              )}

              {f.type === "toggle" && (
                <div className="toggle-row">
                  <input
                    type="checkbox"
                    checked={(values[f.key] ?? f.default) === "true"}
                    onChange={(e) => onChange(f.key, String(e.target.checked))}
                  />
                  <span>{f.label}</span>
                </div>
              )}

              {(f.type === "text" || f.type === "url" || f.type === "number") && (
                <input
                  type={f.type === "url" ? "url" : f.type === "number" ? "number" : "text"}
                  placeholder={f.placeholder}
                  value={values[f.key] ?? f.default ?? ""}
                  onChange={(e) => onChange(f.key, e.target.value)}
                />
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
