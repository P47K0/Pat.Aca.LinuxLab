# CKA Practice Lab

A browser-based environment for drilling CKA exam commands — installing and
upgrading a single-node Kubernetes cluster, plus the Linux tools (`vim`,
`grep`, `awk`, `jq`, ...) the exam actually runs on. Lives at
`lab.koorevaar.com`, a new section of [koorevaar.com](https://www.koorevaar.com).

**Full requirements/architecture decisions:** see the BRD —
https://claude.ai/code/artifact/f9549e0c-5e07-4215-b614-7c1b24a7a771

## Why it's split into "real" and "simulated"

None of Azure's container-hosting options (Container Apps, Container
Instances, Web App for Containers) grant privileged mode or custom Linux
capabilities, and a real `kubeadm`-managed cluster needs both (containerd
has to run *inside* the lab container, which is containers-in-a-container).
So:

- Everything **not** Kubernetes-specific — `vim`, `grep`, `awk`, `sed`,
  `jq`, `tar`, general `systemctl`/`apt-get` use — is completely real.
- `kubeadm`, `kubectl`, and the k8s-specific parts of `apt-get`/`apt-mark`
  are **simulated**: shims in [`simulator/bin`](simulator/bin) that accept
  the real flags, enforce the real step order, and print realistic output,
  without a real cluster underneath. See [`simulator/lib.sh`](simulator/lib.sh)
  for the shared state/progress-reporting helpers, and the BRD's §06 for the
  full reasoning.

Run `simulator/bin` through a quick manual sequence yourself to see it in
action — install → init → `kubectl get nodes` → upgrade plan/apply — the
sequencing is enforced (e.g. `upgrade apply` before `upgrade plan` fails
with a realistic error and a hint).

## Repo layout

```
Dockerfile                     the lab container (Ubuntu 22.04 + real tools + the simulator)
simulator/                     the kubeadm/systemctl/apt-get shims baked into the image
Pat.Aca.LinuxLab.Api/          .NET 10 minimal API + SignalR hub — session lifecycle, terminal relay
Cloudflare/ui-worker/          the frontend: terminal (xterm.js) + live progress checklist
.github/workflows/             manual (workflow_dispatch) build/deploy pipelines
```

## What's built vs. what's a documented placeholder

Built and verified in this environment:
- The simulator shims — actually run end-to-end (install → init → upgrade)
  as a standalone smoke test, sequencing failures included.
- The API project — compiles clean against the real `Azure.ResourceManager.AppContainers`
  / `Azure.Identity` SDKs (`dotnet build`).
- The ui-worker — typechecks clean and passes `wrangler deploy --dry-run`.

Left as **explicit TODOs**, not silently assumed correct:
- `ContainerConsoleClient`'s exec/console WebSocket URL (in
  `Pat.Aca.LinuxLab.Api/Services/ContainerConsoleClient.cs`) is written from
  the documented shape of the API `az containerapp exec` uses, but hasn't
  been run against a real subscription — verify it against Microsoft's
  current REST reference before relying on it.
- The Cloudflare Access identity check now cryptographically verifies
  `Cf-Access-Jwt-Assertion` against Cloudflare's JWKS (`JwtBearer`, wired up
  in `Program.cs`) instead of trusting the header as plain text — but that's
  only a full guarantee once the ACA ingress is also restricted to
  Cloudflare's IP ranges, so the origin can't be reached directly at all.
  Not yet done (see "Manual setup" below).
- No Dockerfile has been built with an actual Docker daemon here (none was
  available in this environment) — the shim logic was verified standalone
  instead. Worth a real `docker build` before first deploy.

## Security & abuse limits

- **No separate API key.** The browser talks to the API's SignalR hub
  directly (not proxied through the Worker), so a Worker-held secret has no
  connection to attach itself to. The boundary is Cloudflare Access's own
  signed JWT instead, verified server-side (see above) — arguably stronger
  than a static shared secret, since it's short-lived and tied to Access's
  own session rather than valid forever until manually rotated.
- **Session-start rate limit**: max 5 new sessions per user per rolling
  hour (`LabSessionOptions.MaxSessionStartsPerHour`) — this is the real
  cost/abuse guard, since starting a session is what spins up a billable
  Container App. `/internal/progress` also has a light fixed-window limit
  (60/min) as basic hygiene.
- **2-hour hard session cap** (`LabSessionOptions.MaxSessionMinutes`),
  separate from the 30-minute idle timeout — deliberately matches the real
  CKA exam's own time limit, so hitting it is itself part of the practice,
  not just a cost control.
- **Captcha on login**: considered and skipped for v1. Cloudflare Access's
  own policy rules (Email, Country, Device posture, IdP groups, ...) don't
  include a native Turnstile/CAPTCHA option, and login is already gated by
  possessing a specific invited email inbox (OTP) for a handful of trusted
  people — low value for the added complexity. If ever needed (e.g. the
  invite list grows), Turnstile can be layered in front of the Access login
  path via a zone-level WAF "Managed Challenge" rule (free plan), no app
  changes required.
- **Not yet built**: per-keystroke flood throttling on `SendInput` itself.
  Low real risk at this scale (a handful of trusted users), but worth
  revisiting if that changes.

## Manual setup (not code — done once, by hand)

1. **Cloudflare Access application** covering both `lab.koorevaar.com` (the
   Worker) and the API's hostname (e.g. `api.lab.koorevaar.com`) under one
   Access app, so a single login covers both. Free tier, email allow-list —
   just the owner for now (BRD §07).
2. **Custom domains**: `lab.koorevaar.com` → this Worker;
   `api.lab.koorevaar.com` → the API's Container App, both proxied through
   Cloudflare (needed for Access to gate them, and for the API to read the
   `Cf-Access-*` headers).
3. **ACA environment**: reuses the existing Container Apps environment from
   `Pat.Aca.BlogServiceApi` (per the BRD's assumptions) — the API needs its
   managed identity granted rights to create/delete Container Apps in that
   resource group.
4. **GitHub secrets/vars** (`dev` environment, same pattern as
   `Pat.Aca.BlogServiceApi`): secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `DOCKERHUB_TOKEN`; vars `DOCKERHUB_USERNAME`,
   `AZURE_RESOURCE_GROUP`.
5. **`Pat.Aca.LinuxLab.Api`'s `LabSession` config** (env vars in production,
   `appsettings.Development.json` locally — never committed non-empty):
   `SubscriptionId`, `ResourceGroup`, `ContainerAppsEnvironmentName`,
   `LabImage` (the pushed `cka-lab` image), `SelfUrl` (this API's own
   reachable URL).
6. **`CloudflareAccess` config** (same non-empty-locally rule):
   `TeamDomain` (Zero Trust → Settings → Custom Pages) and `Audience` (the
   Access application's AUD tag) — needed to verify the JWT.
7. **Restrict the ACA ingress** to Cloudflare's IP ranges (or an equivalent
   network restriction) so the API is only ever reachable through the
   Access-protected hostname, never directly — the JWT check alone doesn't
   stop someone from hitting the raw ACA URL and forging the header
   themselves.

## Local development

```bash
# API
cd Pat.Aca.LinuxLab.Api && dotnet run

# Frontend
cd Cloudflare/ui-worker && npm install && npm run dev

# Simulator shims, without Docker — copy simulator/bin + lib.sh onto your
# PATH and try the install/upgrade sequence directly:
export PATH="$PWD/simulator/bin:$PATH"
apt-get install -y containerd kubelet kubeadm kubectl
systemctl enable --now containerd kubelet
kubeadm init --pod-network-cidr=10.244.0.0/16
kubectl get nodes
```
