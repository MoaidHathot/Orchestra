import { describe, it, expect, beforeEach } from 'vitest';
import { getRespondedBy, setRespondedBy } from './identity';

describe('identity (respondedBy)', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('returns null when nothing has been stored', () => {
    expect(getRespondedBy()).toBeNull();
  });

  it('round-trips a name through localStorage', () => {
    setRespondedBy('alice');
    expect(getRespondedBy()).toBe('alice');
  });

  it('trims surrounding whitespace on read and write', () => {
    setRespondedBy('  bob  ');
    expect(getRespondedBy()).toBe('bob');
  });

  it('treats empty/whitespace-only input as a clear', () => {
    setRespondedBy('alice');
    setRespondedBy('   ');
    expect(getRespondedBy()).toBeNull();
  });

  it('null input clears the entry', () => {
    setRespondedBy('alice');
    setRespondedBy(null);
    expect(getRespondedBy()).toBeNull();
  });

  it('treats a stored empty string as null', () => {
    localStorage.setItem('orchestra.portal.respondedBy', '');
    expect(getRespondedBy()).toBeNull();
  });

  it('uses a namespaced storage key', () => {
    setRespondedBy('alice');
    expect(localStorage.getItem('orchestra.portal.respondedBy')).toBe('alice');
  });
});
