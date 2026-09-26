/**
 * Stable hash-based color assignment for actor (sub-agent) names.
 * Same agentName always produces the same hue across runs/sessions/clients,
 * so users learn to associate a color with a specific sub-agent.
 *
 * Uses HSL with a fixed saturation/lightness pair tuned for both light and
 * dark themes, giving a wide palette while keeping legibility consistent.
 */

/** djb2 string hash — fast, deterministic, evenly distributed for short strings. */
function djb2(str: string): number {
  let hash = 5381;
  for (let i = 0; i < str.length; i++) {
    // hash * 33 ^ char
    hash = ((hash << 5) + hash) ^ str.charCodeAt(i);
  }
  // Force unsigned 32-bit
  return hash >>> 0;
}

/**
 * Returns a stable hue (0–359) for an actor name.
 * Exported separately so tests can assert determinism without locking in HSL strings.
 */
export function hashHue(agentName: string): number {
  return djb2(agentName) % 360;
}

/**
 * HSL color string for an actor. Tuned for borders/badges on the existing
 * Portal theme — saturated enough to read at small sizes, light enough to
 * sit on dark backgrounds without burning.
 */
export function actorColor(agentName: string): string {
  const h = hashHue(agentName);
  return `hsl(${h}, 65%, 60%)`;
}

/**
 * Slightly muted variant of the actor color, suitable for backgrounds.
 */
export function actorBackgroundColor(agentName: string, alpha = 0.12): string {
  const h = hashHue(agentName);
  return `hsla(${h}, 65%, 55%, ${alpha})`;
}
