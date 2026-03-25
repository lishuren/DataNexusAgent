/**
 * Shared utility for compressing file blobs before base64 encoding.
 *
 * Uses the browser-native CompressionStream API (gzip) to reduce payload size
 * for text-based formats (CSV, JSON, text, XML, markdown). Skips already-compressed
 * formats (xlsx, zip, gz, pdf, images) where gzip adds overhead without benefit.
 *
 * The resulting data URL uses "application/gzip" MIME type with an "x-original-type"
 * parameter so the backend can detect compression and restore the original MIME.
 * Format: data:application/gzip;x-original-type=text/csv;base64,...
 */

/** MIME types that are already compressed — gzip won't help. */
const SKIP_COMPRESSION = new Set([
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", // xlsx
  "application/vnd.ms-excel",             // xls
  "application/zip",
  "application/x-zip-compressed",
  "application/gzip",
  "application/x-gzip",
  "application/pdf",
  "image/png",
  "image/jpeg",
  "image/gif",
  "image/webp",
]);

/** Extensions that map to already-compressed formats. */
const SKIP_EXTENSIONS = new Set([
  "xlsx", "xls", "zip", "gz", "tgz", "pdf", "png", "jpg", "jpeg", "gif", "webp",
]);

/** Check whether compression should be skipped based on MIME type or filename. */
function shouldSkipCompression(mimeType: string, fileName?: string): boolean {
  if (SKIP_COMPRESSION.has(mimeType.toLowerCase())) return true;
  if (fileName) {
    const ext = fileName.split(".").pop()?.toLowerCase() ?? "";
    if (SKIP_EXTENSIONS.has(ext)) return true;
  }
  return false;
}

/** Compress a blob using browser-native gzip CompressionStream. */
async function gzipBlob(blob: Blob): Promise<Blob> {
  const cs = new CompressionStream("gzip");
  const compressed = blob.stream().pipeThrough(cs);
  return new Response(compressed).blob();
}

/** Convert a blob to a base64 data URL. */
function blobToBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  });
}

/**
 * Convert a file/blob to a data URL, compressing text-based content with gzip first.
 *
 * For compressible types:
 *   Returns: data:application/gzip;x-original-type={originalMime};base64,...
 *
 * For already-compressed types:
 *   Returns: data:{originalMime};base64,... (standard, uncompressed)
 *
 * @param blob     The file content
 * @param fileName Optional filename (used for extension-based skip detection)
 */
export async function toCompressedDataUrl(blob: Blob, fileName?: string): Promise<string> {
  const mimeType = blob.type || "application/octet-stream";

  if (shouldSkipCompression(mimeType, fileName)) {
    // Already compressed — just base64 encode directly
    return blobToBase64(blob);
  }

  // Compress with gzip
  const compressed = await gzipBlob(blob);

  // Only use compression if it actually reduces size
  if (compressed.size >= blob.size) {
    return blobToBase64(blob);
  }

  // Reuse blobToBase64 to get a standard data URL, then re-wrap with compression metadata
  const rawDataUrl = await blobToBase64(compressed);
  const base64Part = rawDataUrl.substring(rawDataUrl.indexOf(",") + 1);

  return `data:application/gzip;x-original-type=${encodeURIComponent(mimeType)};base64,${base64Part}`;
}
