/**
 * Helpers for downloading a file that the API streams back as a blob (auth-guarded, routed through
 * the gateway) rather than a direct storage link. Kept in one place so recordings, summaries and
 * classroom files behave identically.
 */

/** Extracts the file name from a `Content-Disposition` header, supporting RFC 5987 `filename*`. */
export const filenameFromContentDisposition = (header?: string | null): string | null => {
  if (!header) return null;
  const encoded = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header);
  if (encoded?.[1]) {
    try {
      return decodeURIComponent(encoded[1].replace(/["']/g, '').trim());
    } catch {
      /* fall through to the plain form */
    }
  }
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain?.[1]?.trim() ?? null;
};

/** Saves a blob to disk under the given name via a transient object URL. */
export const triggerBlobDownload = (blob: Blob, fileName: string): void => {
  const objectUrl = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
};
