import { lazy, Suspense, useState } from "react";
import type { UiField } from "@/types/api";

const OneDriveFilePicker = lazy(() => import("./pickers/OneDriveFilePicker"));
const GoogleDriveFilePicker = lazy(() => import("./pickers/GoogleDriveFilePicker"));

interface DynamicFormProps {
  fields: UiField[];
  values: Record<string, string>;
  onChange: (key: string, value: string) => void;
}

export function DynamicForm({ fields, values, onChange }: DynamicFormProps) {
  const [fileNames, setFileNames] = useState<Record<string, string>>({});

  const handleFile = (key: string, file: File) => {
    setFileNames((prev) => ({ ...prev, [key]: file.name }));
    const reader = new FileReader();
    reader.onload = () => onChange(key, reader.result as string);
    reader.readAsDataURL(file);
  };

  return (
    <div className="dynamic-form">
      <div className="form-grid">
        {fields.map((f) => {
          const fullClass = f.type === "textarea" || f.type === "file" || f.type === "onedrive-file" || f.type === "google-drive-file" ? " full" : "";
          return (
            <div key={f.key} className={`form-field${fullClass}`}>
              <label>
                {f.label}
                {f.required && <span style={{ color: "var(--danger)" }}> *</span>}
              </label>

              {f.type === "file" && (
                <div
                  className="file-drop"
                  onClick={(e) => {
                    const input = (e.currentTarget as HTMLElement).querySelector("input");
                    input?.click();
                  }}
                  onDragOver={(e) => {
                    e.preventDefault();
                    e.currentTarget.classList.add("drag-over");
                  }}
                  onDragLeave={(e) => {
                    e.currentTarget.classList.remove("drag-over");
                  }}
                  onDrop={(e) => {
                    e.preventDefault();
                    e.currentTarget.classList.remove("drag-over");
                    const file = e.dataTransfer.files[0];
                    if (!file) return;
                    handleFile(f.key, file);
                  }}
                >
                  {fileNames[f.key]
                    ? <><span style={{ color: "var(--primary)" }}>✔</span> {fileNames[f.key]}</>
                    : <>📁 {f.placeholder ?? "Drop file here or click to browse"}</>}
                  <input
                    type="file"
                    accept={f.accept}
                    style={{ display: "none" }}
                    onChange={(e) => {
                      const file = e.target.files?.[0];
                      if (!file) return;
                      handleFile(f.key, file);
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

              {f.type === "onedrive-file" && (
                <div className="cloud-picker-wrapper">
                  <Suspense fallback={<span className="cloud-picker-loading">Loading OneDrive picker…</span>}>
                    <OneDriveFilePicker
                      accept={f.accept}
                      onChange={(dataUrl) => onChange(f.key, dataUrl)}
                      onFileName={(name) => setFileNames((prev) => ({ ...prev, [f.key]: name }))}
                    />
                  </Suspense>
                  {fileNames[f.key] && (
                    <span className="cloud-picker-selected">
                      <span style={{ color: "var(--primary)" }}>✔</span> {fileNames[f.key]}
                    </span>
                  )}
                </div>
              )}

              {f.type === "google-drive-file" && (
                <div className="cloud-picker-wrapper">
                  <Suspense fallback={<span className="cloud-picker-loading">Loading Google Drive picker…</span>}>
                    <GoogleDriveFilePicker
                      accept={f.accept}
                      onChange={(dataUrl) => onChange(f.key, dataUrl)}
                      onFileName={(name) => setFileNames((prev) => ({ ...prev, [f.key]: name }))}
                    />
                  </Suspense>
                  {fileNames[f.key] && (
                    <span className="cloud-picker-selected">
                      <span style={{ color: "var(--primary)" }}>✔</span> {fileNames[f.key]}
                    </span>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
