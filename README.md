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
- `LabHub`'s Cloudflare Access identity check trusts the
  `Cf-Access-Authenticated-User-Email` header as-is. That's only safe once
  this API is reachable *exclusively* through the Access-protected hostname
  (e.g. by restricting the ACA ingress to Cloudflare's IP ranges) — not yet
  done.
- No Dockerfile has been built with an actual Docker daemon here (none was
  available in this environment) — the shim logic was verified standalone
  instead. Worth a real `docker build` before first deploy.

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
