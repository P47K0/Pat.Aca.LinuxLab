# Backlog

Things worth doing later, not yet committed to. For decisions already made,
see the BRD. For known gaps in what's currently built, see the README's
"What's built vs. what's a documented placeholder" and "Security & abuse
limits" sections — this file is specifically for forward-looking work that
doesn't have a home yet.

## Revisit if needed

- [ ] An independent, external cleanup watchdog (a scheduled GitHub Actions
  job, same Azure CLI pattern as `deploy-api.yml`, force-deleting any
  `lab-*` Container App past its max age) — deliberately deferred on
  2026-08-30 to see how the session-cleanup fix actually behaves first.
  Worth doing if sessions are ever found lingering again.

## Possible future project: network security hardening

Scoped 2026-08-30, not started. A networking-focused Azure Container Apps
project — real portfolio value on its own.