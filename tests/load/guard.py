"""
guard.py — refuse to write to a database that doesn't look like a throwaway test database.

The seeder, the pause/dbload/combined tiers and the release-check tiers write Hangfire rows
directly, lock tables or take the database offline. Before any of that, `ensure_writable`
checks the configured target:

    * the database name contains "test" (case-insensitive), or is listed in
      [safety] allow_databases;
    * the host is this machine (localhost, 127.0.0.1, ::1, ".", "(local)" or the machine name),
      unless [safety] allow_remote = true;

and then asks for confirmation, unless --yes was passed or HFLOAD_ASSUME_YES=1 is set. Without a
terminal to ask on, it refuses instead of assuming yes.

    [safety]
    allow_databases = ["hangfire_load"]   # extra names that don't contain "test"
    allow_remote = false
"""
from __future__ import annotations

import os
import socket
import sys
from dataclasses import dataclass

import click

import config as cfg_mod

_LOCAL_HOSTS = {"localhost", "127.0.0.1", "::1", ".", "(local)", "(localdb)"}


@dataclass(frozen=True)
class Target:
    provider: str
    host: str
    database: str

    def __str__(self) -> str:
        return f"{self.provider} database '{self.database}' on {self.host}"


def _odbc_fields(conn_str: str) -> dict[str, str]:
    """Key/value pairs of an ODBC connection string, keys lower-cased. Braced values may hold ';'."""
    fields: dict[str, str] = {}
    i = 0
    while i < len(conn_str):
        eq = conn_str.find("=", i)
        if eq < 0:
            break
        key = conn_str[i:eq].strip().lower()
        j = eq + 1
        if j < len(conn_str) and conn_str[j] == "{":
            end = conn_str.find("}", j)
            end = len(conn_str) if end < 0 else end
            value = conn_str[j + 1:end]
            semi = conn_str.find(";", end)
            i = len(conn_str) if semi < 0 else semi + 1
        else:
            semi = conn_str.find(";", j)
            semi = len(conn_str) if semi < 0 else semi
            value = conn_str[j:semi]
            i = semi + 1
        if key:
            fields[key] = value.strip()
    return fields


def describe_target(cfg: dict) -> Target:
    provider = cfg_mod.get_db_provider(cfg)
    if provider == "postgresql":
        pg = cfg["database"]["postgresql"]
        return Target(provider, str(pg.get("host", "")), str(pg.get("database", "")))
    if provider == "sqlserver":
        fields = _odbc_fields(cfg_mod.get_sqlserver_conn(cfg))
        host = fields.get("server") or fields.get("data source") or fields.get("address") or ""
        database = fields.get("database") or fields.get("initial catalog") or ""
        return Target(provider, host, database)
    return Target(provider, "", "")


def _host_name(host: str) -> str:
    """'tcp:db01,1433' → 'db01'; '.\\SQL2025' → '.'; 'localhost\\SQL2025' → 'localhost'."""
    name = host.strip()
    if name.lower().startswith(("tcp:", "np:", "lpc:")):
        name = name.split(":", 1)[1]
    for sep in (",", "\\"):
        name = name.split(sep, 1)[0]
    return name.strip().lower()


def _is_local(host: str) -> bool:
    name = _host_name(host)
    if name in _LOCAL_HOSTS:
        return True
    try:
        return name in {socket.gethostname().lower(), socket.getfqdn().lower()}
    except OSError:
        return False


def problems(cfg: dict, target: Target | None = None) -> list[str]:
    """Why the configured database isn't safe to write to; empty when it is."""
    target = target or describe_target(cfg)
    safety = cfg.get("safety", {})
    found: list[str] = []

    if target.provider not in ("postgresql", "sqlserver"):
        found.append(f"provider '{target.provider}' has no database to write to")
        return found

    allowed = {str(n).lower() for n in safety.get("allow_databases", [])}
    if not target.database:
        found.append("no database name in the configuration")
    elif "test" not in target.database.lower() and target.database.lower() not in allowed:
        found.append(f"database '{target.database}' doesn't contain 'test' and isn't in [safety] allow_databases")

    if not safety.get("allow_remote", False) and not _is_local(target.host):
        found.append(f"host '{target.host}' isn't this machine (set [safety] allow_remote = true to allow it)")

    return found


def ensure_writable(cfg: dict, action: str, assume_yes: bool = False) -> Target:
    """Raise a ClickException unless the target is a local test database and the user agreed."""
    target = describe_target(cfg)
    found = problems(cfg, target)
    if found:
        raise click.ClickException(f"Refusing to {action}: " + "; ".join(found) + ".")

    if assume_yes or os.environ.get("HFLOAD_ASSUME_YES") == "1":
        return target
    if not sys.stdin or not sys.stdin.isatty():
        raise click.ClickException(
            f"Refusing to {action} without confirmation: pass --yes (or set HFLOAD_ASSUME_YES=1).")
    click.confirm(f"{action[:1].upper()}{action[1:]} writes to the {target}. Continue?", abort=True)
    return target


def yes_option(func):
    """The --yes / -y flag that skips ensure_writable's prompt."""
    return click.option(
        "--yes", "-y", "assume_yes", is_flag=True,
        help="Don't ask before writing to the test database (the name and host checks still apply).",
    )(func)
