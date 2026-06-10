"""
config.py — Loads configuration for the load-testing tools.

Resolution order (highest priority first):
    1. Environment variables (HFLOAD_*) — ideal for CI and shared machines.
    2. config.toml                      — your local, git-ignored settings.
    3. config.example.toml              — checked-in defaults / fallback.

Environment variable overrides (all optional):
    HFLOAD_BASE_URL          e.g. http://localhost:5100
    HFLOAD_DASHBOARD_PATH    e.g. /hangfire
    HFLOAD_REQUEST_TIMEOUT   seconds (int)
    HFLOAD_DB_PROVIDER       postgresql | sqlserver | inmemory
    HFLOAD_PG_HOST           PostgreSQL host
    HFLOAD_PG_PORT           PostgreSQL port
    HFLOAD_PG_DATABASE       PostgreSQL database
    HFLOAD_PG_USERNAME       PostgreSQL username
    HFLOAD_PG_PASSWORD       PostgreSQL password
    HFLOAD_SQLSERVER_CONNECTION_STRING   full ODBC connection string for pyodbc
    HFLOAD_MONITOR_POLL_INTERVAL         seconds (float)
    HFLOAD_MONITOR_HISTORY_POINTS        int

This means you can run against any environment without editing a file, e.g.:
    set HFLOAD_DB_PROVIDER=sqlserver
    set HFLOAD_SQLSERVER_CONNECTION_STRING=Driver={ODBC Driver 17 for SQL Server};Server=...
    python cli.py check-all
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

if sys.version_info >= (3, 11):
    import tomllib
else:
    try:
        import tomllib
    except ImportError:
        import tomli as tomllib  # type: ignore[no-redef]

_TOOLS_DIR = Path(__file__).parent
_CONFIG_PATH = _TOOLS_DIR / "config.toml"
_EXAMPLE_PATH = _TOOLS_DIR / "config.example.toml"


def _env(name: str) -> str | None:
    """Read an env var, treating empty string as unset."""
    val = os.environ.get(name)
    return val if val else None


def load() -> dict:
    """Load config.toml (or config.example.toml as fallback), then apply any
    HFLOAD_* environment-variable overrides on top."""
    path = _CONFIG_PATH if _CONFIG_PATH.exists() else _EXAMPLE_PATH
    with open(path, "rb") as f:
        cfg = tomllib.load(f)

    _apply_env_overrides(cfg)
    return cfg


def _apply_env_overrides(cfg: dict) -> None:
    """Mutate cfg in place with any HFLOAD_* overrides that are set."""
    cfg.setdefault("app", {})
    cfg.setdefault("database", {})
    cfg["database"].setdefault("postgresql", {})
    cfg["database"].setdefault("sqlserver", {})
    cfg.setdefault("monitor", {})

    app = cfg["app"]
    if v := _env("HFLOAD_BASE_URL"):
        app["base_url"] = v
    if v := _env("HFLOAD_DASHBOARD_PATH"):
        app["dashboard_path"] = v
    if v := _env("HFLOAD_REQUEST_TIMEOUT"):
        app["request_timeout"] = int(v)

    db = cfg["database"]
    if v := _env("HFLOAD_DB_PROVIDER"):
        db["provider"] = v

    pg = db["postgresql"]
    if v := _env("HFLOAD_PG_HOST"):
        pg["host"] = v
    if v := _env("HFLOAD_PG_PORT"):
        pg["port"] = int(v)
    if v := _env("HFLOAD_PG_DATABASE"):
        pg["database"] = v
    if v := _env("HFLOAD_PG_USERNAME"):
        pg["username"] = v
    if v := _env("HFLOAD_PG_PASSWORD"):
        pg["password"] = v

    mssql = db["sqlserver"]
    if v := _env("HFLOAD_SQLSERVER_CONNECTION_STRING"):
        mssql["connection_string"] = v

    mon = cfg["monitor"]
    if v := _env("HFLOAD_MONITOR_POLL_INTERVAL"):
        mon["poll_interval"] = float(v)
    if v := _env("HFLOAD_MONITOR_HISTORY_POINTS"):
        mon["history_points"] = int(v)


def get_base_url(cfg: dict | None = None) -> str:
    cfg = cfg or load()
    return cfg["app"]["base_url"].rstrip("/")


def get_dashboard_url(cfg: dict | None = None) -> str:
    cfg = cfg or load()
    base = get_base_url(cfg)
    path = cfg["app"]["dashboard_path"].strip("/")
    return f"{base}/{path}"


def get_db_provider(cfg: dict | None = None) -> str:
    cfg = cfg or load()
    return cfg["database"]["provider"].lower()


def get_pg_dsn(cfg: dict | None = None) -> str:
    cfg = cfg or load()
    pg = cfg["database"]["postgresql"]
    return (
        f"host={pg['host']} port={pg['port']} "
        f"dbname={pg['database']} user={pg['username']} password={pg['password']}"
    )


def get_sqlserver_conn(cfg: dict | None = None) -> str:
    cfg = cfg or load()
    return cfg["database"]["sqlserver"]["connection_string"]
