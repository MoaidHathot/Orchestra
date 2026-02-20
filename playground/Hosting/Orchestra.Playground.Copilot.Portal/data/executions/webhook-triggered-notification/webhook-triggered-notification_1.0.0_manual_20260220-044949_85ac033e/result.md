## Structured Notification

---

**SUBJECT:** 🔴 [HIGH PRIORITY] CPU Alert: server-01 Exceeded 90% Threshold

---

### Priority: HIGH ⚠️

---

### Summary
CPU usage on **server-01** has exceeded the 90% threshold, indicating potential service degradation. Immediate investigation is required to prevent cascading failures to dependent systems and potential service outages.

---

### Key Details
- **Host:** `server-01`
- **Category:** Alert
- **Condition:** CPU usage > 90%
- **Risk:** Service slowdowns, timeouts, and downstream cascade failures
- **Affected:** Applications, dependent APIs, databases, and microservices on this host

---

### Immediate Impact
- End users may experience latency or unresponsive services
- Load balancers may redistribute traffic, stressing other nodes
- Message queues and job processors may accumulate backlogs

---

### Recommended Next Steps
1. **Investigate** — Run `top` or `htop` to identify CPU-consuming processes
2. **Review** — Check recent deployments or configuration changes
3. **Remediate** — Restart problematic services if safe; consider scaling if load is legitimate
4. **Monitor** — Watch for secondary alerts from dependent health checks

---

### Notification Recipients
| Level | Recipients |
|-------|------------|
| **Immediate** | On-call infrastructure engineer, SRE team |
| **Escalation** | Application owners for server-01 services |
| **Awareness** | NOC/operations dashboard |

---

**Action Required:** Prompt investigation to prevent potential outage.