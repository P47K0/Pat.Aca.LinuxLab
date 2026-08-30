FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    bash \
    coreutils \
    procps \
    net-tools \
    iproute2 \
    dnsutils \
    wget \
    curl \
    vim \
    less \
    jq \
    git \
    apt-transport-https \
    ca-certificates \
    gnupg \
    lsb-release \
    bash-completion \
    sudo \
    && rm -rf /var/lib/apt/lists/*

# No nano on purpose – mimic exam environment

# --- CKA command simulator -------------------------------------------------
# kubeadm/containerd can't run for real in here (no privileged mode on any
# Azure container-PaaS option — see the project's BRD, §06), so install and
# upgrade are simulated by shims on the PATH instead of the real binaries.
# Everything NOT k8s-specific (vim, grep, awk, jq, tar, ...) is untouched
# and genuinely real, including the apt-get shim's fallback to real
# apt-get for non-k8s packages — see the labuser filesystem-ownership
# block near the end of this file for how that's made to actually work
# without real root at runtime.
COPY simulator/lib.sh /usr/local/lab-bin/lib.sh
COPY simulator/bin/ /usr/local/lab-bin/
RUN chmod +x /usr/local/lab-bin/*

# NOT configuring passwordless sudoers here on purpose — confirmed for
# real that it wouldn't matter anyway: Azure Container Apps' exec sessions
# run with the kernel's "no new privileges" flag set, which blocks sudo's
# setuid escalation outright regardless of sudoers config. That's the
# platform working as intended for a service handing invited users a raw
# shell, not a bug to route around. So there is no real root at runtime,
# full stop — instead `sudo` itself is shimmed (simulator/bin/sudo) to
# just run commands as labuser, which is fine because every
# "administrative" surface this lab manages is made labuser-writable at
# build time instead (see the ownership block near the end of this file).
ENV PATH="/usr/local/lab-bin:${PATH}"

# Set per-session by the API when it creates this container (see
# Pat.Aca.LinuxLab.Api's SessionManager) so the simulator's progress events
# reach the right session's checklist. Empty by default: with no API to
# report to, the shims just skip that step and still work standalone.
ENV LAB_API_URL=""
ENV LAB_SESSION_ID=""

RUN useradd -m -s /bin/bash labuser

# labuser gets real write access to the standard install-target trees —
# confirmed necessary for real, not just theorized: a plain `apt-get
# install` as labuser fails immediately on `/var/lib/dpkg/lock-frontend`
# (EACCES, not an explicit "must be root" check), and fixing just that
# would only move the failure one step later, since unpacking a package
# means writing new files into /usr/bin, /usr/share, sometimes /etc —
# none writable by a non-root user in a stock Ubuntu image either. This
# is the one point the image ever has real root (see the sudo shim's
# comment above — there is none at runtime), so it's done here once, at
# build time, rather than attempted at runtime.
#
# This also covers /etc/kubernetes, so the kubeadm shim's fake admin.conf
# (simulator/bin/kubeadm) and the real "cp"/"chown" instructions it
# prints work with no special-casing beyond a plain `mkdir -p` at runtime.
#
# Deliberately not a security regression despite being broad: this
# container is single-tenant and ephemeral (one per session, destroyed on
# logout/idle — BRD §08), and DAC file ownership is orthogonal to actual
# Linux capabilities — labuser still can't do anything requiring real
# privilege (mount, raw sockets, ...), and "no new privileges" above still
# means no setuid binary can newly escalate regardless of who owns it.
# /bin, /sbin, /lib, /lib64 are symlinks into /usr on this base image
# (confirmed via `ls -ld`), so chowning /usr already covers them.
RUN chown -R labuser:labuser /usr /etc /var /opt

USER labuser
WORKDIR /home/labuser

# PID 1 just needs to stay alive — it is NOT the interactive shell. The
# API's exec connection (Pat.Aca.LinuxLab.Api's ContainerConsoleClient)
# starts /bin/bash as a *separate* process inside the running container via
# Azure's exec mechanism (the same way `docker exec`/`kubectl exec` work) —
# it never attaches to PID 1 directly. `bash -l` as PID 1 was wrong:
# without a TTY/stdin attached at container start, it hits EOF and exits
# almost immediately, so the container never stayed up long enough to
# become "ready" for exec at all (confirmed for real: IsReady stayed false
# for the full 60s poll window, not just slow to start).
CMD ["sleep", "infinity"]
