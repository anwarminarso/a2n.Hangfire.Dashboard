"""
db.py — Direct database inspector for Hangfire storage (PostgreSQL & SQL Server).

Usage:
    python db.py stats
    python db.py jobs --state failed --limit 20
    python db.py queues
    python db.py recurring
    python db.py servers
    python db.py tags
    python db.py cleanup --days 7
    python db.py query "SELECT COUNT(*) FROM hangfire.job"
"""
from __future__ import annotations

import sys
from datetime import datetime, timedelta, timezone
from typing import Any

import click
from rich import box
from rich.console import Console
from rich.panel import Panel
from rich.table import Table

import config as cfg_mod

console = Console()


# ─── DB Connection ────────────────────────────────────────────────────────────

def get_connection(provider: str | None = None):
    """Return a DB-API 2.0 connection for the configured provider."""
    conf = cfg_mod.load()
    prov = provider or cfg_mod.get_db_provider(conf)

    if prov == "postgresql":
        try:
            import psycopg  # psycopg3
            dsn = cfg_mod.get_pg_dsn(conf)
            return psycopg.connect(dsn), "postgresql"
        except ImportError:
            try:
                import psycopg2  # fallback
                pg = conf["database"]["postgresql"]
                return psycopg2.connect(
                    host=pg["host"], port=pg["port"], dbname=pg["database"],
                    user=pg["username"], password=pg["password"]
                ), "postgresql"
            except ImportError:
                console.print("[red]Install psycopg or psycopg2: pip install psycopg[binary][/red]")
                sys.exit(1)

    elif prov == "sqlserver":
        try:
            import pyodbc
            conn_str = cfg_mod.get_sqlserver_conn(conf)
            return pyodbc.connect(conn_str), "sqlserver"
        except ImportError:
            console.print("[red]Install pyodbc: pip install pyodbc[/red]")
            sys.exit(1)

    else:
        console.print(f"[red]Unsupported provider: {prov}. Use 'postgresql' or 'sqlserver'.[/red]")
        sys.exit(1)


def _schema_prefix(provider: str) -> str:
    """Hangfire tables are in 'hangfire' schema for PostgreSQL, 'HangFire' for SQL Server."""
    if provider == "postgresql":
        return "hangfire."
    return "[HangFire]."


def _ph(provider: str) -> str:
    """Parameter placeholder: psycopg uses %s, pyodbc uses ?."""
    return "?" if provider == "sqlserver" else "%s"


def _utcnow_minus(provider: str, minutes: int) -> str:
    """Provider-specific 'UTC now minus N minutes' SQL expression."""
    if provider == "sqlserver":
        return f"DATEADD(MINUTE, -{minutes}, GETUTCDATE())"
    return f"NOW() - INTERVAL '{minutes} minutes'"


def _set_table(provider: str) -> str:
    """The Hangfire 'Set' table — a reserved word in SQL Server, needs brackets."""
    return "[HangFire].[Set]" if provider == "sqlserver" else "hangfire.set"


def _col(provider: str, name: str) -> str:
    """Bracket-quote columns that are reserved words in SQL Server (key, value)."""
    if provider == "sqlserver":
        return f"[{name}]"
    return name


# ─── Queries ──────────────────────────────────────────────────────────────────

def fetch_stats(conn, provider: str) -> dict[str, Any]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    stats: dict[str, Any] = {}

    # Job counts by state
    cur.execute(f"""
        SELECT statename, COUNT(*) AS cnt
        FROM {s}job
        WHERE statename IS NOT NULL
        GROUP BY statename
        ORDER BY cnt DESC
    """)
    stats["jobs_by_state"] = cur.fetchall()

    # Total jobs
    cur.execute(f"SELECT COUNT(*) FROM {s}job")
    stats["total_jobs"] = cur.fetchone()[0]

    # Recurring jobs
    cur.execute(f"SELECT COUNT(*) FROM {_set_table(provider)} WHERE {_col(provider,'key')} = 'recurring-jobs'")
    stats["recurring_count"] = cur.fetchone()[0]

    # Servers
    cur.execute(f"SELECT COUNT(*) FROM {s}server WHERE lastheartbeat > {_utcnow_minus(provider, 5)}")
    stats["active_servers"] = cur.fetchone()[0]

    # Queue depths
    cur.execute(f"""
        SELECT queue, COUNT(*) AS depth
        FROM {s}jobqueue
        WHERE fetchedat IS NULL
        GROUP BY queue
        ORDER BY depth DESC
    """)
    stats["queue_depths"] = cur.fetchall()

    cur.close()
    return stats


def fetch_jobs(conn, provider: str, state: str | None, limit: int) -> list[tuple]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    ph = _ph(provider)

    where = f"WHERE j.statename = {ph}" if state else ""
    params = (state,) if state else ()

    if provider == "sqlserver":
        cur.execute(f"""
            SELECT TOP {limit} j.Id, j.StateName, j.CreatedAt, j.ExpireAt,
                   LEFT(j.InvocationData, 120) AS invocation
            FROM {s}Job j
            {where}
            ORDER BY j.CreatedAt DESC
        """, params)
    else:
        cur.execute(f"""
            SELECT j.id, j.statename, j.createdat, j.expireat,
                   LEFT(j.invocationdata, 120) AS invocation
            FROM {s}job j
            {where}
            ORDER BY j.createdat DESC
            LIMIT {limit}
        """, params)
    rows = cur.fetchall()
    cur.close()
    return rows


def fetch_queues(conn, provider: str) -> list[tuple]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    if provider == "sqlserver":
        cur.execute(f"""
            SELECT queue,
                   SUM(CASE WHEN FetchedAt IS NULL THEN 1 ELSE 0 END)     AS enqueued,
                   SUM(CASE WHEN FetchedAt IS NOT NULL THEN 1 ELSE 0 END) AS fetched
            FROM {s}JobQueue
            GROUP BY queue
            ORDER BY enqueued DESC
        """)
    else:
        cur.execute(f"""
            SELECT queue,
                   COUNT(*) FILTER (WHERE fetchedat IS NULL)   AS enqueued,
                   COUNT(*) FILTER (WHERE fetchedat IS NOT NULL) AS fetched
            FROM {s}jobqueue
            GROUP BY queue
            ORDER BY enqueued DESC
        """)
    rows = cur.fetchall()
    cur.close()
    return rows


def fetch_recurring(conn, provider: str) -> list[tuple]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    if provider == "sqlserver":
        cur.execute(f"""
            SELECT h.[Key], h.[Field], h.[Value]
            FROM {s}Hash h
            WHERE h.[Key] LIKE 'recurring-job:%'
            ORDER BY h.[Key], h.[Field]
        """)
    else:
        cur.execute(f"""
            SELECT h.key, h.field, h.value
            FROM {s}hash h
            WHERE h.key LIKE 'recurring-job:%'
            ORDER BY h.key, h.field
        """)
    rows = cur.fetchall()
    cur.close()

    # Group by job key
    jobs: dict[str, dict] = {}
    for key, field, value in rows:
        job_id = key.replace("recurring-job:", "")
        if job_id not in jobs:
            jobs[job_id] = {"id": job_id}
        jobs[job_id][field] = value
    return list(jobs.values())


def fetch_servers(conn, provider: str) -> list[tuple]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    cur.execute(f"""
        SELECT id, data, lastheartbeat
        FROM {s}server
        ORDER BY lastheartbeat DESC
    """)
    rows = cur.fetchall()
    cur.close()
    return rows


def fetch_tags(conn, provider: str, limit: int) -> list[tuple]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    # Hangfire.Tags stores tags in the Set table with key="tags:<tag>"
    if provider == "sqlserver":
        cur.execute(f"""
            SELECT TOP {limit} REPLACE([Key], 'tags:', '') AS tag, COUNT(*) AS job_count
            FROM {s}[Set]
            WHERE [Key] LIKE 'tags:%'
            GROUP BY [Key]
            ORDER BY job_count DESC
        """)
    else:
        cur.execute(f"""
            SELECT REPLACE(key, 'tags:', '') AS tag, COUNT(*) AS job_count
            FROM {s}set
            WHERE key LIKE 'tags:%'
            GROUP BY key
            ORDER BY job_count DESC
            LIMIT {limit}
        """)
    rows = cur.fetchall()
    cur.close()
    return rows


def fetch_failed_details(conn, provider: str, limit: int) -> list[tuple]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    if provider == "sqlserver":
        cur.execute(f"""
            SELECT TOP {limit} j.Id,
                   j.CreatedAt,
                   LEFT(s.Reason, 80) AS reason,
                   LEFT(j.InvocationData, 100) AS invocation
            FROM {s}Job j
            JOIN {s}State s ON j.StateId = s.Id
            WHERE j.StateName = 'Failed'
            ORDER BY j.CreatedAt DESC
        """)
    else:
        cur.execute(f"""
            SELECT j.id,
                   j.createdat,
                   LEFT(s.reason, 80) AS reason,
                   LEFT(j.invocationdata, 100) AS invocation
            FROM {s}job j
            JOIN {s}state s ON j.stateid = s.id
            WHERE j.statename = 'Failed'
            ORDER BY j.createdat DESC
            LIMIT {limit}
        """)
    rows = cur.fetchall()
    cur.close()
    return rows


def do_cleanup(conn, provider: str, days: int) -> dict[str, int]:
    s = _schema_prefix(provider)
    cur = conn.cursor()
    ph = _ph(provider)
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)

    if provider == "sqlserver":
        cur.execute(f"""
            DELETE FROM {s}Job
            WHERE ExpireAt IS NOT NULL AND ExpireAt < {ph}
        """, (cutoff,))
        deleted_jobs = cur.rowcount

        cur.execute(f"""
            DELETE FROM {s}State
            WHERE JobId NOT IN (SELECT Id FROM {s}Job)
        """)
        deleted_states = cur.rowcount
    else:
        cur.execute(f"""
            DELETE FROM {s}job
            WHERE expireat IS NOT NULL AND expireat < {ph}
        """, (cutoff,))
        deleted_jobs = cur.rowcount

        cur.execute(f"""
            DELETE FROM {s}state
            WHERE jobid NOT IN (SELECT id FROM {s}job)
        """)
        deleted_states = cur.rowcount

    conn.commit()
    cur.close()
    return {"jobs": deleted_jobs, "states": deleted_states}


# ─── CLI ──────────────────────────────────────────────────────────────────────

@click.group()
def cli():
    """Direct database inspector for Hangfire storage."""
    pass


@cli.command()
def stats():
    """Show job counts, queue depths, and server status."""
    conn, provider = get_connection()
    try:
        data = fetch_stats(conn, provider)
    finally:
        conn.close()

    console.print(Panel(f"[bold cyan]Database Statistics[/bold cyan] — provider: {provider}"))

    # Jobs by state
    t = Table(title="Jobs by State", box=box.ROUNDED, show_header=True)
    t.add_column("State", style="cyan")
    t.add_column("Count", justify="right", style="bold")
    for state_name, cnt in data["jobs_by_state"]:
        color = {
            "Succeeded": "green",
            "Failed": "red",
            "Processing": "yellow",
            "Enqueued": "blue",
            "Scheduled": "magenta",
            "Deleted": "dim",
        }.get(state_name, "white")
        t.add_row(f"[{color}]{state_name}[/{color}]", str(cnt))
    t.add_row("[bold]Total[/bold]", f"[bold]{data['total_jobs']}[/bold]")
    console.print(t)

    # Queue depths
    if data["queue_depths"]:
        q = Table(title="Queue Depths (enqueued)", box=box.ROUNDED)
        q.add_column("Queue", style="cyan")
        q.add_column("Depth", justify="right")
        for queue, depth in data["queue_depths"]:
            q.add_row(queue, str(depth))
        console.print(q)

    console.print(f"\n[green]Recurring jobs:[/green] {data['recurring_count']}")
    console.print(f"[green]Active servers:[/green] {data['active_servers']}")


@cli.command()
@click.option("--state", "-s", default=None, help="Filter by state (Succeeded, Failed, Processing, ...)")
@click.option("--limit", "-n", default=20, show_default=True, help="Max rows to return")
def jobs(state: str | None, limit: int):
    """List jobs, optionally filtered by state."""
    conn, provider = get_connection()
    try:
        rows = fetch_jobs(conn, provider, state, limit)
    finally:
        conn.close()

    title = f"Jobs" + (f" — state={state}" if state else "")
    t = Table(title=title, box=box.ROUNDED, show_header=True)
    t.add_column("ID", style="dim")
    t.add_column("State", style="cyan")
    t.add_column("Created At")
    t.add_column("Expires At")
    t.add_column("Invocation", no_wrap=False, max_width=60)

    for row in rows:
        job_id, state_name, created, expires, invocation = row
        color = {"Succeeded": "green", "Failed": "red", "Processing": "yellow"}.get(state_name or "", "white")
        t.add_row(
            str(job_id),
            f"[{color}]{state_name or '—'}[/{color}]",
            str(created)[:19] if created else "—",
            str(expires)[:19] if expires else "—",
            (invocation or "")[:100],
        )
    console.print(t)


@cli.command()
def queues():
    """Show queue depths (enqueued vs fetched)."""
    conn, provider = get_connection()
    try:
        rows = fetch_queues(conn, provider)
    finally:
        conn.close()

    t = Table(title="Queues", box=box.ROUNDED)
    t.add_column("Queue", style="cyan")
    t.add_column("Enqueued", justify="right")
    t.add_column("Fetched", justify="right")
    for queue, enqueued, fetched in rows:
        t.add_row(queue, str(enqueued), str(fetched))
    console.print(t)


@cli.command()
def recurring():
    """List recurring jobs from the Hash table."""
    conn, provider = get_connection()
    try:
        jobs_list = fetch_recurring(conn, provider)
    finally:
        conn.close()

    t = Table(title="Recurring Jobs", box=box.ROUNDED)
    t.add_column("ID", style="cyan")
    t.add_column("Cron")
    t.add_column("Queue")
    t.add_column("Next Exec")
    t.add_column("Last Exec")
    t.add_column("Last State")

    for j in jobs_list:
        t.add_row(
            j.get("id", "?"),
            j.get("Cron", "—"),
            j.get("Queue", "—"),
            (j.get("NextExecution") or "—")[:19],
            (j.get("LastExecution") or "—")[:19],
            j.get("LastJobState") or "—",
        )
    console.print(t)


@cli.command()
def servers():
    """List Hangfire servers and their last heartbeat."""
    conn, provider = get_connection()
    try:
        rows = fetch_servers(conn, provider)
    finally:
        conn.close()

    t = Table(title="Servers", box=box.ROUNDED)
    t.add_column("ID", style="cyan")
    t.add_column("Last Heartbeat")
    t.add_column("Data", max_width=80)

    for server_id, data, heartbeat in rows:
        age = ""
        if heartbeat:
            delta = datetime.now(timezone.utc) - heartbeat.replace(tzinfo=timezone.utc)
            age = f"  ({int(delta.total_seconds())}s ago)"
        t.add_row(str(server_id), str(heartbeat)[:19] + age, (data or "")[:80])
    console.print(t)


@cli.command()
@click.option("--limit", "-n", default=30, show_default=True)
def tags(limit: int):
    """Show tag cloud with job counts."""
    conn, provider = get_connection()
    try:
        rows = fetch_tags(conn, provider, limit)
    finally:
        conn.close()

    t = Table(title=f"Top {limit} Tags", box=box.ROUNDED)
    t.add_column("Tag", style="cyan")
    t.add_column("Job Count", justify="right")
    for tag, count in rows:
        t.add_row(tag, str(count))
    console.print(t)


@cli.command()
@click.option("--limit", "-n", default=20, show_default=True)
def failures(limit: int):
    """Show recent failed jobs with error reasons."""
    conn, provider = get_connection()
    try:
        rows = fetch_failed_details(conn, provider, limit)
    finally:
        conn.close()

    t = Table(title=f"Failed Jobs (last {limit})", box=box.ROUNDED)
    t.add_column("ID", style="dim")
    t.add_column("Created At")
    t.add_column("Reason", style="red", max_width=80)
    t.add_column("Invocation", max_width=60)

    for job_id, created, reason, invocation in rows:
        t.add_row(str(job_id), str(created)[:19], reason or "—", (invocation or "")[:60])
    console.print(t)


@cli.command()
@click.argument("sql")
def query(sql: str):
    """Run a raw SQL query and display results."""
    conn, provider = get_connection()
    try:
        cur = conn.cursor()
        cur.execute(sql)
        rows = cur.fetchall()
        col_names = [desc[0] for desc in cur.description] if cur.description else []
        cur.close()
    finally:
        conn.close()

    if not col_names:
        console.print(f"[green]Query executed. No results returned.[/green]")
        return

    t = Table(title=f"Query Result ({len(rows)} rows)", box=box.ROUNDED)
    for col in col_names:
        t.add_column(str(col))
    for row in rows:
        t.add_row(*[str(v) if v is not None else "NULL" for v in row])
    console.print(t)


@cli.command()
@click.option("--days", "-d", default=7, show_default=True, help="Delete expired jobs older than N days")
@click.confirmation_option(prompt="This will DELETE expired jobs from the database. Continue?")
def cleanup(days: int):
    """Delete expired jobs from the database."""
    conn, provider = get_connection()
    try:
        result = do_cleanup(conn, provider, days)
    finally:
        conn.close()

    console.print(f"[green]Cleanup complete:[/green]")
    console.print(f"  Deleted jobs:   {result['jobs']}")
    console.print(f"  Orphan states:  {result['states']}")


if __name__ == "__main__":
    cli()
