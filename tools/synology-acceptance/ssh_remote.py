"""Pinned, key-only NAS access for existing Paramiko/SFTP workflows."""
import os
from pathlib import Path
import re
import shlex

import paramiko


def connect(alias="hsoffice-nas", config_path=None):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", alias):
        raise ValueError("Expected a local SSH host alias.")
    settings = paramiko.SSHConfig.from_path(
        str(config_path or Path.home() / ".ssh" / "config")).lookup(alias)
    identities = settings.get("identityfile", [])
    known_hosts = shlex.split(settings.get("userknownhostsfile", ""), posix=True)
    if (len(identities) != 1 or len(known_hosts) != 1
            or not settings.get("user") or not settings.get("hostname")
            or settings.get("proxycommand") or settings.get("proxyjump")):
        raise ValueError("NAS alias requires one identity, one pinned host file and a direct endpoint.")
    client = paramiko.SSHClient()
    try:
        client.load_host_keys(str(Path(known_hosts[0]).expanduser()))
        client.set_missing_host_key_policy(paramiko.RejectPolicy())
        client.connect(settings["hostname"], port=int(settings.get("port", "22")),
                       username=settings["user"],
                       key_filename=str(Path(identities[0]).expanduser()),
                       allow_agent=False, look_for_keys=False,
                       timeout=10, auth_timeout=15, banner_timeout=10)
        return client
    except BaseException:
        client.close()
        raise


def run(client, command, sudo=False, timeout=30):
    password = os.environ.get("DSM_CODEX_PASSWORD", "")
    if sudo:
        if not password or "\n" in password or "\r" in password:
            raise ValueError("Supply the existing NAS sudo credential in DSM_CODEX_PASSWORD.")
        command = "sudo -k -S -p '' -- /bin/sh -c " + shlex.quote(command)
    stdin, stdout, stderr = client.exec_command(command, timeout=timeout)
    if sudo:
        stdin.write(password + "\n")
        stdin.flush()
    stdin.channel.shutdown_write()
    output = stdout.read().decode("utf-8", "replace")
    error = stderr.read().decode("utf-8", "replace")
    if password:
        output = output.replace(password, "[redacted]")
        error = error.replace(password, "[redacted]")
    return stdout.channel.recv_exit_status(), output, error
