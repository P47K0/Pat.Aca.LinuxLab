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
  `jq`, `tar`, general `systemctl`/`apt-get` use — is completely real, with
  one exception: `sudo` itself is also shimmed (see below).
- `kubeadm`, `kubectl`, and the k8s-specific parts of `apt-get`/`apt-mark`
  are **simulated**: shims in [`simulator/bin`](simulator/bin) that accept
  the real flags, enforce the real step order, and print realistic output,
  without a real cluster underneath. See [`simulator/lib.sh`](simulator/lib.sh)
  for the shared state/progress-reporting helpers, and the BRD's §06 for the
  full reasoning.
- `sudo` is shimmed too, for an unrelated reason: Azure Container Apps'
  exec sessions run with the kernel's "no new privileges" flag set, which
  blocks sudo's setuid escalation outright — confirmed for real against a
  live session (`sudo: The "no new privileges" flag is set...`). Real root
  is never available here regardless of sudoers config, so
  `simulator/bin/sudo` just runs the given command as `labuser` instead of
  erroring on every `sudo ...` a real exam workflow types out of habit.
  Transparent for everything the simulator already manages (package/
  service/cluster state, plus `/etc/kubernetes` — pre-created
  `labuser`-owned at image build time for exactly this reason). It's a
  real, known gap for anything else: e.g. `sudo apt-get install -y
  <non-k8s package>` now fails with a `dpkg`/permission error instead of
  sudo's clearer one, since actually installing a package still needs real
  root this container can't grant — see the TODO below.

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
- The in-page **Feedback** button posts to the Worker's own `/feedback`
  route, which forwards in-process to the `koorevaar.com` Worker's
  existing contact-form handler via a Cloudflare **service binding**
  (`CONTACT_WORKER` in `wrangler.toml`, target `contact`) — no public URL,
  no CORS. Request/response shapes (`{name, email, subject, message}` in,
  `{success, message}` out) and the `/api/contact` path are confirmed
  directly from that Worker's own source and its contact page's JS, not
  guessed.

Left as **explicit TODOs**, not silently assumed correct:
- `ContainerConsoleClient`'s exec connection (in
  `Pat.Aca.LinuxLab.Api/Services/ContainerConsoleClient.cs`) reads
  `ContainerAppReplicaContainer.ExecEndpoint` straight off the Azure SDK
  rather than hand-constructing a URL — confirmed to exist via reflection
  against the real installed `Azure.ResourceManager.AppContainers` package
  (an earlier hand-built URL guess, based on `az containerapp exec`'s
  internal implementation, got a real 404 against a live app). The exact
  scheme/query-string shape `ExecEndpoint` returns is still unverified
  against a live subscription — still worth a close look at the first real
  connection attempt.
- The Cloudflare Access identity check now cryptographically verifies
  `Cf-Access-Jwt-Assertion` against Cloudflare's JWKS (`JwtBearer`, wired up
  in `Program.cs`) instead of trusting the header as plain text — but that's
  only a full guarantee once the ACA ingress is also restricted to
  Cloudflare's IP ranges, so the origin can't be reached directly at all.
  Not yet done (see "Manual setup" below).
- No Dockerfile has been built with an actual Docker daemon here (none was
  available in this environment) — the shim logic was verified standalone
  instead. Worth a real `docker build` before first deploy.
- Real (non-simulated) `apt-get install` of arbitrary packages — e.g.
  `sudo apt-get install -y tmux` — was fixed at the filesystem-permission
  level (`labuser` now owns `/usr`, `/var`, `/opt`, and most of `/etc` —
  see the Dockerfile), but still untested against a real `docker build` +
  live install, same caveat as the line above.
- The footer's build counter (`Cloudflare/ui-worker` — see "Manual
  setup" step 3) needs a real KV namespace ID in place of the
  `REPLACE_WITH_REAL_KV_NAMESPACE_ID` placeholder in `wrangler.toml`
  before it'll show anything — the code degrades gracefully without it
  (just hides that part of the footer), but hasn't been exercised
  against a real namespace yet.

## Security & abuse limits

- **No separate API key.** The browser talks to the API's SignalR hub
  directly (not proxied through the Worker), so a Worker-held secret has no
  connection to attach itself to. The boundary is Cloudflare Access's own
  signed JWT instead, verified server-side (see above) — arguably stronger
  than a static shared secret, since it's short-lived and tied to Access's
  own session rather than valid forever until manually rotated.
- **Session-start rate limit**: max 15 new sessions per user per rolling
  hour (`LabSessionOptions.MaxSessionStartsPerHour`, overridable via the
  optional `LAB_MAX_SESSION_STARTS_PER_HOUR` var — see step 6 below) —
  this is the real cost/abuse guard, since starting a session is what
  spins up a billable Container App. Counts *starts*, not concurrent
  sessions, so a dropped connection's automatic reconnect (the frontend's
  `withAutomaticReconnect()`) counts as a new one too, same as a manual
  page reload — worth knowing if this ever trips during heavy testing.
  Rejection sends a generic "try again in N minutes" message computed
  from the real rolling window (`SessionRateLimitExceededException`),
  not the raw internal message (which includes the email, log-only).
  `/internal/progress` also has a light fixed-window limit (60/min) as
  basic hygiene.
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
- **Network isolation between sessions, and restriction of the lab
  container's outbound internet access, are not yet implemented.

## Manual setup (not code — done once, by hand)

0. **Activate Cloudflare Zero Trust** on the account, if it isn't already
   (one-time per account, not per project — skip if you've used Access
   before): Cloudflare dashboard → **Zero Trust** → **Get started** →
   **Free plan** → **Activate**. It asks for a credit card even though the
   free plan itself is free — that's just how Cloudflare gates the Zero
   Trust product, not a sign you're about to be charged.

   **Also add the One-time PIN identity provider here, if the account
   doesn't already have one configured** (also one-time per account) —
   confirmed for real that this isn't on by default just from activating
   Zero Trust, and skipping it is exactly what leaves an app with no
   usable login method at all (see the app-level step below): **Zero
   Trust → Identity Providers → Add an identity provider → One-time
   PIN**. No further configuration needed — it's Cloudflare's own
   built-in email-code login, not an external IdP.
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
   3. **On the application itself, go to its Login Methods tab** and
      either switch on **"Accept all available identity providers"** or
      explicitly select **One-time PIN** in the **Choose identity
      providers** list. Easy to miss since the app otherwise looks fully
      configured without it — confirmed for real: skipping this step is
      what leaves the app's login page with no usable way in at all
      (invited users land on a generic Cloudflare page instead of an
      email-code prompt, with no obvious link back to "add the One-time
      PIN identity provider from step 0 above").
   4. *Then* the Worker's own **Access** tab → **Protect this Worker** will
      let you pick that already-created policy (`Owner`) instead of being
      limited to the two canned options.
   5. On the application's **Additional settings** (same tab as the AUD
      tag), enable **"Bypass options requests to origin."** Without this,
      Access intercepts the CORS preflight `OPTIONS` request itself before
      it ever reaches the API — the preflight can't carry the Access auth
      cookie (browsers don't send credentials on preflight requests, only
      on the real one), so Access blocks it outright, and neither the
      app-level nor the ACA ingress-level CORS config (step 4) ever gets a
      chance to respond. Safe to enable specifically because both of those
      are already configured — the origin handles CORS enforcement itself.
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

   **Create the KV namespace the footer's build counter uses**, if you
   want that (optional — the page works fine without it, the counter
   just won't show): **Workers & Pages → KV → Create a namespace**
   (any name, e.g. `cka-lab-build-info`), then put its ID in
   `Cloudflare/ui-worker/wrangler.toml`'s `[[kv_namespaces]]` block in
   place of the `REPLACE_WITH_REAL_KV_NAMESPACE_ID` placeholder. No
   API token needed — the Worker reads/writes it through a plain
   binding, same mechanism as the `CONTACT_WORKER` service binding
   above. Deliberately computed this way instead of pulling a real git
   SHA: Workers Builds' own build-time variables (`WORKERS_CI_COMMIT_SHA`
   et al.) aren't accessible at runtime, and it doesn't honor a custom
   build command from `wrangler.toml` at all — only from the dashboard
   — so getting a real commit SHA onto the page would need a manual,
   undocumented-in-this-repo dashboard step instead of just this one
   config value. The `[version_metadata]` binding's version ID, by
   contrast, changes on every deploy and is genuinely available at
   runtime with zero extra configuration beyond the binding itself.
4. **Custom domains**: `lab.koorevaar.com` → this Worker (handled by the
   Git integration in step 3); `api.lab.koorevaar.com` → the API's
   Container App, bound via **ACA → Networking → Custom domains**.
   For the API's domain specifically:
   1. Add the domain there; Azure gives you a **TXT** record
      (`asuid.api.lab` → a verification ID) for ownership validation — add
      it in Cloudflare DNS as **DNS-only**, proxy status doesn't matter for
      TXT records either way.
   2. Add the **CNAME** (`api.lab` → the Container App's default
      `*.azurecontainerapps.io` hostname) as **DNS-only (grey cloud) at
      first, not proxied**. Azure's CNAME-target check does its own live
      DNS lookup — a proxied record resolves to Cloudflare's edge IPs
      instead of the real target and fails validation even though the
      record is otherwise correct.
   3. Once Azure validates and binds the domain (managed certificate
      included), **switch the CNAME to Proxied (orange cloud)** — that's
      what actually puts Cloudflare, and Access, in the traffic path.

   While in that same **Networking** blade: also set **CORS** —
   `lab.koorevaar.com` and `api.lab.koorevaar.com` are different origins
   (subdomains count), and the browser's SignalR negotiate call is
   cross-origin. This is ACA's own ingress-level CORS feature, separate
   from (and more robust than) the API's own `LabSession:AllowedOrigin`
   app-level CORS policy — it handles the preflight `OPTIONS` request
   before it ever reaches the app or the hub's `[Authorize]` check, so set
   it here rather than relying on the app-level policy alone: **Allowed
   Origins** `https://lab.koorevaar.com`, **Allow credentials** enabled
   (required — the frontend calls with `withCredentials: true`), **Allowed
   Methods** at least `GET, POST, OPTIONS`, **Allowed Headers** `*`. Like
   the container's env vars, updating this via CLI overwrites the whole
   policy rather than merging — pass the complete set each time.

   Both ending up proxied is what's needed for Access to gate them and for
   the API to read the `Cf-Access-*` headers — just not during the CNAME's
   own validation step.
5. **ACA environment**: The API needs its
   managed identity granted rights to create/delete Container Apps in that
   resource group.
6. **GitHub secrets/vars** (`dev` environment, same pattern as
   `Pat.Aca.BlogServiceApi`): secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `DOCKERHUB_TOKEN`; vars `DOCKERHUB_USERNAME`,
   `AZURE_RESOURCE_GROUP`, `ACA_ENVIRONMENT_NAME`, `CF_ACCESS_TEAM_DOMAIN`,
   `CF_ACCESS_AUDIENCE` (from step 2), `LAB_FRONTEND_ORIGIN`
   (`https://lab.koorevaar.com` — needed for CORS, since the browser calls
   this API cross-origin from a different subdomain). `LAB_MAX_SESSION_STARTS_PER_HOUR`
   is optional — the workflow falls back to `15` if it's unset, so there's
   nothing to add here just to get a working deploy; set it only if you
   actually want a different limit, and re-run the workflow for a value
   change to reach the running app (see "Security & abuse limits" above).
   `API_SELF_URL` is chicken-and-egg
   (unknown until the first create prints the app's FQDN) — leave it unset
   for the first `deploy-api.yml` run, then set it and re-run. Find it as
   **Application Url** on the Container App's Overview page in the portal
   (or in the workflow's own log output) — and use that raw
   `*.azurecontainerapps.io` URL, **not** the `api.lab.koorevaar.com`
   custom domain. `SelfUrl` is only ever used for internal container-to-
   container calls (the simulator shims' progress callbacks to
   `/internal/progress`, which is deliberately unauthenticated, unlike the
   SignalR hub) — routing that through Cloudflare would be pointless, and
   would actually break once step 8's ingress restriction is in place,
   since that internal traffic doesn't originate from a Cloudflare IP.
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
