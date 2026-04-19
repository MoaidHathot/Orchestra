import { describe, it, expect } from 'vitest';
import { hashHue, actorColor, actorBackgroundColor } from './actorColors';

describe('actorColors', () => {
  describe('hashHue', () => {
    it('is deterministic — same name returns same hue across calls', () => {
      const a = hashHue('writer');
      const b = hashHue('writer');
      expect(a).toBe(b);
    });

    it('produces a hue in the [0, 359] range', () => {
      const names = ['writer', 'reviewer', 'researcher', 'planner', 'a', '', 'x'.repeat(200)];
      for (const name of names) {
        const hue = hashHue(name);
        expect(hue).toBeGreaterThanOrEqual(0);
        expect(hue).toBeLessThan(360);
        expect(Number.isInteger(hue)).toBe(true);
      }
    });

    it('produces different hues for distinct realistic agent names', () => {
      // Not strictly guaranteed by hashing, but for these distinct names the
      // djb2 hash should land on different hues. If this ever flakes, swap
      // the fixtures rather than weakening the assertion.
      const names = ['writer', 'reviewer', 'researcher', 'planner', 'critic'];
      const hues = new Set(names.map(hashHue));
      // At minimum we need 4 distinct hues out of 5 to call the distribution useful.
      expect(hues.size).toBeGreaterThanOrEqual(4);
    });

    it('handles unicode and symbols without throwing', () => {
      expect(() => hashHue('агент-1')).not.toThrow();
      expect(() => hashHue('🤖-bot')).not.toThrow();
      expect(hashHue('агент-1')).toBe(hashHue('агент-1'));
    });
  });

  describe('actorColor', () => {
    it('returns an HSL string with the deterministic hue', () => {
      const color = actorColor('writer');
      const hue = hashHue('writer');
      expect(color).toBe(`hsl(${hue}, 65%, 60%)`);
    });

    it('is stable across calls', () => {
      expect(actorColor('reviewer')).toBe(actorColor('reviewer'));
    });
  });

  describe('actorBackgroundColor', () => {
    it('returns an HSLA string with the deterministic hue and default alpha', () => {
      const bg = actorBackgroundColor('writer');
      const hue = hashHue('writer');
      expect(bg).toBe(`hsla(${hue}, 65%, 55%, 0.12)`);
    });

    it('honors a custom alpha', () => {
      const bg = actorBackgroundColor('writer', 0.4);
      expect(bg).toContain('0.4');
    });
  });
});
