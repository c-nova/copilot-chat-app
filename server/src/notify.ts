import { config } from './config';

/**
 * EXPERIMENTAL: best-effort push notification via ntfy (https://ntfy.sh, or a self-hosted
 * instance) when a chat turn finishes - lets you know Copilot replied without needing to keep the
 * client app open or in the foreground. This is entirely optional and off by default: if
 * NTFY_TOPIC isn't set in server/.env, notifyReplyReady() is a no-op.
 *
 * Uses ntfy's plain HTTP publish API (a single POST, see https://docs.ntfy.sh/publish/) rather
 * than any SDK or API key - ntfy's own server forwards the push to APNs (iOS)/FCM (Android) on
 * your behalf, so this app never needs its own Apple/Google push credentials or a paid Apple
 * Developer Program membership (which real APNs registration would otherwise require).
 *
 * The reply text is sent as the plain POST body (not an HTTP header), so it's safe for it to
 * contain non-ASCII text (e.g. Japanese) or newlines - only the fixed, ASCII-only Title/Tags
 * headers below need to be header-safe.
 *
 * Failures here are logged but never thrown - a notification failing to send must never affect
 * the actual chat response the client already received over the WebSocket.
 */
export async function notifyReplyReady(text: string): Promise<void> {
  if (!config.ntfyTopic) return;

  const url = `${config.ntfyServer}/${encodeURIComponent(config.ntfyTopic)}`;
  const preview = text.trim().replace(/\s+/g, ' ').slice(0, 400) || '(empty reply)';

  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        Title: 'Copilot replied',
        Tags: 'robot',
      },
      body: preview,
    });
    if (!res.ok) {
      console.warn(`[notify] ntfy publish failed: ${res.status} ${res.statusText}`);
    }
  } catch (err: any) {
    console.warn(`[notify] Failed to publish ntfy notification: ${err?.message ?? err}`);
  }
}
