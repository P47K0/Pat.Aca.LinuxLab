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

`--help`/`-h` works on `kubeadm`, `kubectl`, and `systemctl` (including
per-subcommand, e.g. `kubeadm upgrade apply --help`), same as the real
exam's reference material — and works regardless of cluster state, so
`kubeadm upgrade plan --help` doesn't require a cluster to already exist.
Deliberately honest about scope, though: it only lists what this simulator
actually implements (no `join`/`token`/`certs`/`config`), rather than
mirroring real kubeadm's full help and setting a trap for commands that'd
then fail. `apt-get --help`/`apt-mark --help` already fall through to the
real binaries unmodified — nothing to add there.

## Repo layout

```
Dockerfile                     the lab container (Ubuntu 22.04 + real tools + the simulator)
                                — never deployed as a standing app; see build-lab-image.yml
simulator/                     the kubeadm/systemctl/apt-get shims baked into the image
Pat.Aca.LinuxLab.Api/          .NET 10 minimal API + SignalR hub — session lifecycle, terminal relay
Cloudflare/ui-worker/          the frontend: terminal (xterm.js) + live progress checklist
                                (deployed via Cloudflare's own Git integration — see "Manual setup")
.github/workflows/             manual (workflow_dispatch) pipelines for the lab image + API only
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

0. **Activate Cloudflare Zero Trust** on the account, if it isn't already
   (one-time per account, not per project — skip if you've used Access
   before): Cloudflare dashboard → **Zero Trust** → **Get started** →
   **Free plan** → **Activate**. It asks for a credit card even though the
   free plan itself is free — that's just how Cloudflare gates the Zero
   Trust product, not a sign you're about to be charged.
1. **Cloudflare Access application** covering both `lab.koorevaar.com` (the
   Worker) and the API's hostname (e.g. `api.lab.koorevaar.com`) under one
   Access app, so a single login covers both. Free tier, email allow-list —
   just the owner for now (BRD §07).

   **Create the policy through the full wizard, not the Worker's Access
   tab shortcut.** The Worker's own "Protect this Worker behind Access"
   button is fine to use *last*, once a real policy already exists — but
   creating the policy directly from that shortcut only offers two canned
   options ("Cloudflare account" — anyone who's a member of your whole
   Cloudflare account, not just this app — or "Email domain" — anyone at
   that domain, which on Gmail/Hotmail is the entire public). Neither
   matches the individual-email allow-list this project needs. Instead:

   1. **Zero Trust → Access → Applications → Add an application →
      Self-hosted**. This one application covers both hostnames: use
      **Add Workers** to select the ui-worker (scope: production + preview
      URLs), and **Add public hostname** (next to it) for
      `api.lab.koorevaar.com`, the API's Container App — confirmed this
      single-app, two-target setup actually works in the current UI.
   2. Under **Access policy**, **Create new policy**: name it (e.g.
      `Owner`), action **Allow**, then under **Include** set the selector
      to **`Emails`** (not "Emails ending in") and add your specific
      address — not a domain. Save the policy, then save the application.
   3. *Then* the Worker's own **Access** tab → **Protect this Worker** will
      let you pick that already-created policy (`Owner`) instead of being
      limited to the two canned options.
2. **Note the Access app's `TeamDomain` and `Audience`** while you're
   already in that dashboard — steps 6 and 7 below both need these two
   values. `TeamDomain` is account-level: Zero Trust → **Settings** (shown
   as "Team domain", e.g. `mute-recipe-da12.cloudflareaccess.com` — add
   `https://` when you actually set the config value, since `Program.cs`
   builds a full URL from it). `Audience` is specific to the application
   you just created in step 1, not account-level — find it under that
   app's **Additional settings** tab, labeled **AUD tag**.
3. **Connect `Cloudflare/ui-worker` via Cloudflare's Git integration**
   (Workers Builds), pointed at this repo with **root directory set to
   `Cloudflare/ui-worker`** — same pattern as `Pat.Aca.BlogServiceApi`'s
   workers. It builds and deploys itself on push; there's no
   `wrangler deploy` step or GitHub Actions workflow for it in this repo.
4. **Custom domains**: `lab.koorevaar.com` → this Worker;
   `api.lab.koorevaar.com` → the API's Container App, both proxied through
   Cloudflare (needed for Access to gate them, and for the API to read the
   `Cf-Access-*` headers).
5. **ACA environment**: reuses the existing Container Apps environment from
   `Pat.Aca.BlogServiceApi` (per the BRD's assumptions) — the API needs its
   managed identity granted rights to create/delete Container Apps in that
   resource group.
6. **GitHub secrets/vars** (`dev` environment, same pattern as
   `Pat.Aca.BlogServiceApi`): secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `DOCKERHUB_TOKEN`; vars `DOCKERHUB_USERNAME`,
   `AZURE_RESOURCE_GROUP`, `ACA_ENVIRONMENT_NAME`, `CF_ACCESS_TEAM_DOMAIN`,
   `CF_ACCESS_AUDIENCE` (from step 2). `API_SELF_URL` is chicken-and-egg
   (unknown until the first create prints the app's FQDN) — leave it unset
   for the first `deploy-api.yml` run, then set it and re-run.
7. **`Pat.Aca.LinuxLab.Api`'s local config** (`appsettings.Development.json`
   — never committed non-empty): `LabSession` (`SubscriptionId`,
   `ResourceGroup`, `ContainerAppsEnvironmentName`, `LabImage`, `SelfUrl`)
   and `CloudflareAccess` (`TeamDomain`/`Audience` from step 2) — production
   gets the same values as env vars via `deploy-api.yml` (step 6).
8. **Restrict the ACA ingress** to Cloudflare's IP ranges (or an equivalent
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
