**🟡 INFRASTRUCTURE DEGRADED — 4/5 Systems Healthy**

| System | Status |
|--------|--------|
| API Gateway | 🟢 Healthy |
| Database Cluster | 🟡 Degraded |
| Cache | 🟢 Healthy |
| Message Queue | 🟢 Healthy |
| Storage | 🟢 Healthy |

**Active Alerts:**
- Database connection pool at 93.5% (threshold: 90%)
- Replica-2 replication lag: 2.3s

**Attention Required:**
- **Database Cluster** — connection pool near exhaustion, replica sync delayed

**Recommended Actions:**
1. Scale database connection pool capacity (immediate)
2. Investigate replica-2 performance (within 24h)

**Trend:** Below baseline. Database pressure indicates increased load or potential resource leak — monitor for escalation.

*Priority: Medium — no outage risk, but may worsen under peak load.*