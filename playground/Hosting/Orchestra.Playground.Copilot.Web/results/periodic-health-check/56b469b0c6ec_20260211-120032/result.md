**System DEGRADED — 4/5 services healthy, database cluster experiencing performance issues.**

**Attention Required:**
- **Database Cluster** — 2 active alerts:
  - `replica-2` replication lag (1.8s) → stale read risk
  - Connection pool at 87% → capacity pressure

**Recommended Actions:**
| Priority | Action |
|----------|--------|
| 🔴 High | Investigate replica-2 lag; check network/disk I/O |
| 🟡 Medium | Scale connection pool or optimize query load |
| 🟢 Low | Set up alerting threshold at 80% pool usage |

**Trend:** ⚠️ Slightly below baseline — connection pool creeping up suggests increased traffic or connection leaks. Monitor over next 2-4 hours.

**Bottom Line:** No outages, but database strain warrants proactive scaling before peak hours.