"""
jobs.py — Hangfire job seeder via direct database INSERT (no app running required)
           or via HTTP trigger when the app is running.

Usage:
    # Seed jobs directly into the DB (no app needed)
    python jobs.py seed --count 20 --type all
    python jobs.py seed --type failing --count 5
    python jobs.py seed --type simple --count 50 --queue critical

    # Trigger recurring jobs via DB (mark NextExecution = now)
    python jobs.py trigger-recurring --id simple-job
    python jobs.py trigger-recurring --all

    # Show job distribution
    python jobs.py stats

    # Purge all jobs in a specific state
    python jobs.py purge --state Failed
    python jobs.py purge --state Succeeded --older-than 1  # days

Job types: simple | console | tagged | failing | scheduled | all
"""
from __future__ import annotations

import json
import random
import sys
import uuid
from datetime import datetime, timedelta, timezone
from typing import Literal

import click
from rich import box
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn
from rich.table import Table

import config as cfg_mod
from db import get_connection, _schema_prefix

console = Console()

JobType = Literal["simple", "console", "tagged", "failing", "scheduled", "all"]

# ─── Invocation data templates ────────────────────────────────────────────────

def _invocation(type_name: str, method: str, args: list | None = None) -> str:
    """Build a Hangfire-style invocation JSON string."""
    return json.dumps({
        "t": type_name,
        "m": method,
        "a": [json.dumps(a) if not isinstance(a, str) else a for a in (args or [])],
        "pn": "Hangfire",
    })


INVOCATIONS = {
    "simple": _invocation("SampleApp.SharedJobs.SampleJobs, SampleApp.SharedJobs", "SimpleJob"),
    "console": _invocation("SampleApp.SharedJobs.SampleJobs, SampleApp.SharedJobs", "ConsoleJob", [None]),
    "tagged": _invocation("SampleApp.SharedJobs.SampleJobs, SampleApp.SharedJobs", "TaggedJob", [None]),
    "failing": _invocation("SampleApp.SharedJobs.SampleJobs, SampleApp.SharedJobs", "FailingJob"),
    "long": _invocation("SampleApp.SharedJobs.SampleJobs, SampleApp.SharedJobs", "LongRunningJob", [None]),
}

TAGS_BY_TYPE = {
    "tagged": ["orders", "processing"],
    "simple": [],
    "console": [],
    "failing": ["error", "test"],
    "long": ["long-running"],
}


# ─── State builders ───────────────────────────────────────────────────────────

def _succeeded_state(job_id: str, created: datetime) -> dict:
    return {
        "stateName": "Succeeded",
        "reason": "The job has been completed successfully.",
        "data": json.dumps({
            "SucceededAt": created.isoformat(),
            "PerformanceDuration": str(random.randint(100, 5000)),
            "Latency": str(random.randint(10, 500)),
        }),
    }


def _failed_state(job_id: str) -> dict:
    return {
        "stateName": "Failed",
        "reason": "InvalidOperationException: This job is designed to fail for testing purposes.",
        "data": json.dumps({
            "FailedAt": datetime.now(timezone.utc).isoformat(),
            "ExceptionType": "System.InvalidOperationException",
            "ExceptionMessage": "This job is designed to fail for testing purposes.",
            "ExceptionDetails": "System.InvalidOperationException: This job is designed to fail\n   at SampleApp.SharedJobs.SampleJobs.FailingJob()",
        }),
    }


def _enqueued_state(queue: str) -> dict:
    return {
        "stateName": "Enqueued",
        "reason": None,
        "data": json.dumps({
            "EnqueuedAt": datetime.now(timezone.utc).isoformat(),
            "Queue": queue,
        }),
    }


def _scheduled_state(enqueue_at: datetime) -> dict:
    return {
        "stateName": "Scheduled",
        "reason": None,
        "data": json.dumps({
            "EnqueueAt": enqueue_at.isoformat(),
            "ScheduledAt": datetime.now(timezone.utc).isoformat(),
        }),
    }


# ─── Insertions ───────────────────────────────────────────────────────────────

def _insert_job(
    cur,
    provider: str,
    invocation: str,
    state: dict,
    queue: str = "default",
    created_offset_hours: float = 0,
    expire_hours: float = 24,
) -> str:
    """Insert a single job + its state, return the job ID."""
    s = _schema_prefix(provider)
    now = datetime.now(timezone.utc) - timedelta(hours=created_offset_hours)
    expire = now + timedelta(hours=expire_hours)

    if provider == "postgresql":
        cur.execute(f"""
            INSERT INTO {s}job (invocationdata, arguments, createdat, expireat, statename)
            VALUES (%s, %s, %s, %s, %s)
            RETURNING id
        """, (invocation, "[]", now, expire, state["stateName"]))
        job_id = str(cur.fetchone()[0])

        cur.execute(f"""
            INSERT INTO {s}state (jobid, name, reason, createdat, data)
            VALUES (%s, %s, %s, %s, %s)
            RETURNING id
        """, (job_id, state["stateName"], state.get("reason"), now, state.get("data")))
        state_id = cur.fetchone()[0]

        cur.execute(f"UPDATE {s}job SET stateid = %s WHERE id = %s", (state_id, job_id))

        # Enqueue into jobqueue if enqueued state
        if state["stateName"] == "Enqueued":
            cur.execute(f"""
                INSERT INTO {s}jobqueue (jobid, queue)
                VALUES (%s, %s)
            """, (job_id, queue))

        # Schedule in set if scheduled state
        if state["stateName"] == "Scheduled":
            enqueue_at_ts = (now + timedelta(hours=1)).timestamp()
            cur.execute(f"""
                INSERT INTO {s}set (key, score, value)
                VALUES ('schedule', %s, %s)
                ON CONFLICT DO NOTHING
            """, (enqueue_at_ts, job_id))

    else:  # sqlserver
        cur.execute(f"""
            INSERT INTO {s}Job (InvocationData, Arguments, CreatedAt, ExpireAt, StateName)
            OUTPUT INSERTED.Id
            VALUES (?, ?, ?, ?, ?)
        """, (invocation, "[]", now, expire, state["stateName"]))
        job_id = str(cur.fetchone()[0])

        cur.execute(f"""
            INSERT INTO {s}State (JobId, Name, Reason, CreatedAt, Data)
            OUTPUT INSERTED.Id
            VALUES (?, ?, ?, ?, ?)
        """, (job_id, state["stateName"], state.get("reason"), now, state.get("data")))
        state_id = cur.fetchone()[0]

        cur.execute(f"UPDATE {s}Job SET StateId = ? WHERE Id = ?", (state_id, job_id))

        # Enqueue into JobQueue if enqueued state
        if state["stateName"] == "Enqueued":
            cur.execute(f"""
                INSERT INTO {s}JobQueue (JobId, Queue)
                VALUES (?, ?)
            """, (job_id, queue))

        # Schedule in Set if scheduled state. SQL Server has no ON CONFLICT, and the
        # Set table has a unique (Key, Value); guard with a NOT EXISTS insert.
        if state["stateName"] == "Scheduled":
            enqueue_at_ts = (now + timedelta(hours=1)).timestamp()
            cur.execute(f"""
                INSERT INTO {s}[Set] ([Key], Score, [Value])
                SELECT 'schedule', ?, ?
                WHERE NOT EXISTS (
                    SELECT 1 FROM {s}[Set] WHERE [Key] = 'schedule' AND [Value] = ?
                )
            """, (enqueue_at_ts, job_id, job_id))

    return job_id


def _insert_tags(cur, provider: str, job_id: str, tags: list[str]):
    """Insert tags for a job (Hangfire.Tags format)."""
    s = _schema_prefix(provider)
    now = datetime.now(timezone.utc)
    expire = now + timedelta(hours=24)
    for tag in tags:
        if provider == "postgresql":
            cur.execute(f"""
                INSERT INTO {s}set (key, score, value, expireat)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT DO NOTHING
            """, (f"tags:{tag}", 0.0, job_id, expire))
        else:  # sqlserver
            cur.execute(f"""
                INSERT INTO {s}[Set] ([Key], Score, [Value], ExpireAt)
                SELECT ?, ?, ?, ?
                WHERE NOT EXISTS (
                    SELECT 1 FROM {s}[Set] WHERE [Key] = ? AND [Value] = ?
                )
            """, (f"tags:{tag}", 0.0, job_id, expire, f"tags:{tag}", job_id))


# ─── Seed orchestration ───────────────────────────────────────────────────────

TYPE_CONFIGS = {
    "simple": {"invocation_key": "simple", "state_factory": lambda: _enqueued_state("default"), "tags": []},
    "console": {"invocation_key": "console", "state_factory": lambda: _succeeded_state("", datetime.now(timezone.utc)), "tags": []},
    "tagged": {"invocation_key": "tagged", "state_factory": lambda: _succeeded_state("", datetime.now(timezone.utc)), "tags": ["orders", "processing"]},
    "failing": {"invocation_key": "failing", "state_factory": lambda: _failed_state(""), "tags": ["error", "test"]},
    "scheduled": {"invocation_key": "simple", "state_factory": lambda: _scheduled_state(datetime.now(timezone.utc) + timedelta(hours=1)), "tags": []},
}


def seed_jobs(
    conn,
    provider: str,
    job_type: str,
    count: int,
    queue: str,
    spread_hours: float = 48,
) -> int:
    """Insert `count` jobs of `job_type` (or random mix if 'all'). Returns inserted count."""
    cur = conn.cursor()
    inserted = 0
    types = list(TYPE_CONFIGS.keys()) if job_type == "all" else [job_type]

    with Progress(
        SpinnerColumn(),
        TextColumn("[cyan]{task.description}"),
        BarColumn(),
        TextColumn("{task.completed}/{task.total}"),
        console=console,
    ) as progress:
        task = progress.add_task(f"Inserting {count} {job_type} job(s)…", total=count)

        for i in range(count):
            t = random.choice(types)
            cfg_entry = TYPE_CONFIGS[t]
            inv = INVOCATIONS[cfg_entry["invocation_key"]]
            state = cfg_entry["state_factory"]()
            tags = cfg_entry["tags"]
            offset_hours = random.uniform(0, spread_hours)

            try:
                job_id = _insert_job(cur, provider, inv, state, queue=queue, created_offset_hours=offset_hours)
                if tags:
                    _insert_tags(cur, provider, job_id, tags)
                inserted += 1
            except Exception as e:
                console.print(f"[yellow]Warning:[/yellow] failed to insert job: {e}")

            progress.update(task, advance=1)

    conn.commit()
    cur.close()
    return inserted


# ─── CLI ──────────────────────────────────────────────────────────────────────

@click.group()
def cli():
    """Job seeder and management tools."""
    pass


@cli.command()
@click.option(
    "--type", "job_type",
    type=click.Choice(["simple", "console", "tagged", "failing", "scheduled", "all"]),
    default="all", show_default=True,
    help="Type of jobs to seed.",
)
@click.option("--count", "-n", default=20, show_default=True, help="Number of jobs to insert.")
@click.option("--queue", "-q", default="default", show_default=True, help="Queue name for enqueued jobs.")
@click.option("--spread-hours", default=48.0, show_default=True, help="Spread created timestamps over N hours.")
def seed(job_type: str, count: int, queue: str, spread_hours: float):
    """Seed test jobs directly into the database."""
    conn, provider = get_connection()
    try:
        inserted = seed_jobs(conn, provider, job_type, count, queue, spread_hours)
    finally:
        conn.close()

    console.print(f"\n[green]✓ Inserted {inserted} job(s) into [{provider}][/green]")


@cli.command()
@click.option("--state", "-s", required=True,
              type=click.Choice(["Succeeded", "Failed", "Deleted", "Enqueued", "Scheduled"]),
              help="State of jobs to purge.")
@click.option("--older-than", default=None, type=float, help="Only purge jobs older than N days.")
@click.confirmation_option(prompt="This will DELETE jobs from the database. Continue?")
def purge(state: str, older_than: float | None):
    """Delete all jobs in a given state from the database."""
    conn, provider = get_connection()
    s = _schema_prefix(provider)
    cur = conn.cursor()

    where_clauses = ["statename = %s"]
    params: list = [state]
    if older_than is not None:
        cutoff = datetime.now(timezone.utc) - timedelta(days=older_than)
        where_clauses.append("createdat < %s")
        params.append(cutoff)

    where = " AND ".join(where_clauses)
    cur.execute(f"DELETE FROM {s}job WHERE {where}", params)
    deleted = cur.rowcount
    conn.commit()
    cur.close()
    conn.close()

    console.print(f"[green]Deleted {deleted} job(s) in state '{state}'.[/green]")


@cli.command("trigger-recurring")
@click.option("--id", "job_id", default=None, help="Recurring job ID to trigger.")
@click.option("--all", "trigger_all", is_flag=True, help="Trigger all recurring jobs.")
def trigger_recurring(job_id: str | None, trigger_all: bool):
    """Force recurring jobs to run now by setting NextExecution to the past."""
    if not job_id and not trigger_all:
        console.print("[red]Specify --id <id> or --all[/red]")
        sys.exit(1)

    conn, provider = get_connection()
    s = _schema_prefix(provider)
    cur = conn.cursor()

    past = (datetime.now(timezone.utc) - timedelta(minutes=1)).isoformat()

    if trigger_all:
        cur.execute(f"""
            UPDATE {s}hash SET value = %s
            WHERE key LIKE 'recurring-job:%%' AND field = 'NextExecution'
        """, (past,))
        count = cur.rowcount
        conn.commit()
        console.print(f"[green]Triggered {count} recurring job(s) (NextExecution set to past).[/green]")
    else:
        cur.execute(f"""
            UPDATE {s}hash SET value = %s
            WHERE key = %s AND field = 'NextExecution'
        """, (past, f"recurring-job:{job_id}"))
        count = cur.rowcount
        conn.commit()
        if count:
            console.print(f"[green]Triggered recurring job '{job_id}'.[/green]")
        else:
            console.print(f"[yellow]Recurring job '{job_id}' not found in hash table.[/yellow]")

    cur.close()
    conn.close()


@cli.command()
def stats():
    """Show current job distribution in the database."""
    conn, provider = get_connection()
    s = _schema_prefix(provider)
    cur = conn.cursor()

    cur.execute(f"""
        SELECT statename, COUNT(*) FROM {s}job
        WHERE statename IS NOT NULL
        GROUP BY statename ORDER BY COUNT(*) DESC
    """)
    rows = cur.fetchall()
    cur.close()
    conn.close()

    t = Table(title="Job Distribution", box=box.ROUNDED)
    t.add_column("State", style="cyan")
    t.add_column("Count", justify="right")
    total = sum(r[1] for r in rows)
    for state_name, cnt in rows:
        bar = "█" * min(int(cnt / max(total, 1) * 30), 30)
        t.add_row(state_name or "—", f"{cnt:,}  [dim]{bar}[/dim]")
    t.add_row("[bold]Total[/bold]", f"[bold]{total:,}[/bold]")
    console.print(t)


if __name__ == "__main__":
    cli()
