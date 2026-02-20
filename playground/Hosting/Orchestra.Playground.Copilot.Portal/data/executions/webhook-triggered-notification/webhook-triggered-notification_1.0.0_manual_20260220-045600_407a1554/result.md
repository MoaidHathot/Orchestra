# 🔴 HIGH PRIORITY NOTIFICATION

---

## Subject: [HIGH] CPU Alert - server-01 Exceeding 90% Threshold

---

### Summary
A high-severity CPU alert has been triggered on **server-01**, with usage exceeding the 90% threshold. Immediate investigation is required to prevent service degradation or potential outage. Action should be taken within **15 minutes**.

---

### Key Details
- **Host**: server-01
- **Issue**: CPU usage exceeded 90%
- **Severity**: High
- **Category**: Alert
- **Immediate Risk**: Application slowdowns, timeouts, potential process crashes

---

### Impact
- Services on server-01 may be unresponsive or experiencing latency
- Dependent systems and APIs may fail or queue requests
- Risk of cascade failure if part of a load-balanced cluster
- Database connections may timeout causing transaction failures

---

### Recommended Next Steps
1. **NOW**: SSH to server-01 and run `top` or `htop` to identify top CPU consumers
2. **If safe**: Kill runaway processes or scale horizontally
3. **Investigate**: Check recent deployments, scheduled cron jobs, or traffic anomalies
4. **Follow-up**: Consider adding 80% threshold alert for earlier warning

---

### Notify
| Role | Action Required |
|------|-----------------|
| On-call SRE | Respond immediately |
| Application Owner | Root cause analysis |
| Platform Team | Infrastructure review |

---

**Priority**: 🔴 **HIGH** — Respond within 15 minutes