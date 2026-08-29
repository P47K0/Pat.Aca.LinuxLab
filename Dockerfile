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
# Everything NOT k8s-specific (vim, grep, awk, jq, tar, real apt installs of
# anything else, ...) is untouched and genuinely real.
COPY simulator/lib.sh /usr/local/lab-bin/lib.sh
COPY simulator/bin/ /usr/local/lab-bin/
RUN chmod +x /usr/local/lab-bin/*

# Real sudo, passwordless — this container is single-user and ephemeral
# (one per session, destroyed on logout/idle, see the BRD's §08), so the
# usual reasons to require a password don't apply. secure_path is extended
# so `sudo kubeadm ...` / `sudo systemctl ...` still resolve to the shims
# above instead of falling through to (nonexistent) real binaries.
RUN echo 'labuser ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/labuser \
    && echo 'Defaults secure_path="/usr/local/lab-bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"' > /etc/sudoers.d/secure_path \
    && chmod 0440 /etc/sudoers.d/labuser /etc/sudoers.d/secure_path

ENV PATH="/usr/local/lab-bin:${PATH}"

# Set per-session by the API when it creates this container (see
# Pat.Aca.LinuxLab.Api's SessionManager) so the simulator's progress events
# reach the right session's checklist. Empty by default: with no API to
# report to, the shims just skip that step and still work standalone.
ENV LAB_API_URL=""
ENV LAB_SESSION_ID=""

RUN useradd -m -s /bin/bash labuser

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
