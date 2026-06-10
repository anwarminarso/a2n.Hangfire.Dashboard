"""
signalr.py — SignalR hub tester for HangfireDashboard's realtime feed.

Connects to /hangfire/hubs/dashboard and subscribes to metrics and/or analytics.
Prints every message received in real-time.

Usage:
    python signalr.py connect
    python signalr.py connect --group analytics
    python signalr.py connect --group both --duration 30
    python signalr.py stress --connections 5 --duration 20
"""
from __future__ import annotations

import asyncio
import json
import signal
import sys
import time
from datetime import datetime

import click
from rich.console import Console
from rich.panel import Panel
from rich.table import Table
from rich import box

import config as cfg_mod

console = Console()


# ─── SignalR client (pure WebSocket, no signalrcore dependency) ───────────────

SIGNALR_HANDSHAKE = json.dumps({"protocol": "json", "version": 1}) + "\x1e"
RECORD_SEP = "\x1e"


async def connect_hub(
    hub_url: str,
    groups: list[str],
    duration: float,
    on_message,
    connection_id: int = 0,
):
    """
    Connect to a SignalR hub over WebSocket (JSON protocol).
    Subscribes to the given groups, calls on_message for each received message.
    Runs for `duration` seconds (0 = until Ctrl+C).
    """
    import websockets  # type: ignore

    console.print(f"[cyan][conn {connection_id}][/cyan] Connecting to {hub_url} …")
    try:
        async with websockets.connect(
            hub_url,
            additional_headers={"User-Agent": "HangfireDashboard-TestTool/1.0"},
            open_timeout=10,
        ) as ws:
            # Handshake
            await ws.send(SIGNALR_HANDSHAKE)
            raw = await asyncio.wait_for(ws.recv(), timeout=10)
            # Strip record separator and parse
            handshake_resp = json.loads(raw.rstrip(RECORD_SEP))
            if "error" in handshake_resp:
                console.print(f"[red]Handshake error: {handshake_resp['error']}[/red]")
                return

            console.print(f"[green][conn {connection_id}] Handshake OK[/green]")

            # Subscribe to requested groups
            invoke_id = 1
            for group in groups:
                method = f"SubscribeTo{'Metrics' if group == 'metrics' else 'Analytics'}"
                msg = json.dumps({
                    "type": 1,
                    "invocationId": str(invoke_id),
                    "target": method,
                    "arguments": [],
                }) + RECORD_SEP
                await ws.send(msg)
                invoke_id += 1
                console.print(f"[cyan][conn {connection_id}][/cyan] Subscribed to {group}")

            start = time.monotonic()
            msg_count = 0

            async def receive_loop():
                nonlocal msg_count
                async for raw_msg in ws:
                    for frame in raw_msg.split(RECORD_SEP):
                        frame = frame.strip()
                        if not frame:
                            continue
                        try:
                            parsed = json.loads(frame)
                        except json.JSONDecodeError:
                            continue
                        msg_type = parsed.get("type")
                        if msg_type == 6:  # ping
                            pong = json.dumps({"type": 6}) + RECORD_SEP
                            await ws.send(pong)
                            continue
                        if msg_type == 1:  # invocation from server
                            msg_count += 1
                            await on_message(connection_id, parsed)
                    if duration > 0 and (time.monotonic() - start) >= duration:
                        break

            if duration > 0:
                try:
                    await asyncio.wait_for(receive_loop(), timeout=duration + 2)
                except asyncio.TimeoutError:
                    pass
            else:
                await receive_loop()

            console.print(f"[dim][conn {connection_id}] Received {msg_count} messages.[/dim]")

    except ConnectionRefusedError:
        console.print(f"[red][conn {connection_id}] Connection refused. Is the app running?[/red]")
    except Exception as e:
        console.print(f"[red][conn {connection_id}] Error: {e}[/red]")


# ─── Message formatters ───────────────────────────────────────────────────────

def _format_metrics(data: dict) -> str:
    """Format MetricsUpdated payload."""
    lines = []
    for key, val in data.items():
        lines.append(f"  [cyan]{key}[/cyan]: {val}")
    return "\n".join(lines)


async def default_message_handler(connection_id: int, msg: dict):
    """Print incoming SignalR messages to console."""
    target = msg.get("target", "?")
    args = msg.get("arguments", [])
    ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]

    console.print(f"[dim]{ts}[/dim] [bold yellow][conn {connection_id}][/bold yellow] → [green]{target}[/green]")

    if args:
        payload = args[0] if len(args) == 1 else args
        try:
            formatted = json.dumps(payload, indent=2)
            # Truncate very long payloads
            if len(formatted) > 800:
                formatted = formatted[:800] + "\n  … (truncated)"
            console.print(f"[dim]{formatted}[/dim]")
        except Exception:
            console.print(f"  {payload}")


# ─── Stress test stats ────────────────────────────────────────────────────────

class StressStats:
    def __init__(self, n: int):
        self.n = n
        self.connected = 0
        self.messages: dict[int, int] = {i: 0 for i in range(n)}
        self.errors: dict[int, str] = {}

    async def on_message(self, conn_id: int, msg: dict):
        self.messages[conn_id] = self.messages.get(conn_id, 0) + 1

    def print_summary(self):
        t = Table(title=f"Stress Test — {self.n} connections", box=box.ROUNDED)
        t.add_column("Connection", justify="right")
        t.add_column("Messages", justify="right")
        t.add_column("Status")
        total = 0
        for i in range(self.n):
            msgs = self.messages.get(i, 0)
            total += msgs
            err = self.errors.get(i)
            status = f"[red]{err[:40]}[/red]" if err else "[green]OK[/green]"
            t.add_row(str(i), str(msgs), status)
        t.add_row("[bold]Total[/bold]", f"[bold]{total}[/bold]", "")
        console.print(t)


# ─── CLI ──────────────────────────────────────────────────────────────────────

@click.group()
def cli():
    """SignalR hub tester for HangfireDashboard realtime feed."""
    pass


@cli.command()
@click.option(
    "--group", "-g",
    type=click.Choice(["metrics", "analytics", "both"]),
    default="metrics", show_default=True,
    help="Subscription group(s) to join.",
)
@click.option("--duration", "-d", default=0, show_default=True,
              help="How many seconds to listen (0 = until Ctrl+C).")
def connect(group: str, duration: int):
    """Connect to the SignalR hub and print all received messages."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    hub_url = dashboard.replace("http://", "ws://").replace("https://", "wss://")
    hub_url = hub_url.rstrip("/") + "/hubs/dashboard"

    groups = ["metrics", "analytics"] if group == "both" else [group]

    console.print(Panel(
        f"[cyan]Hub URL:[/cyan] {hub_url}\n"
        f"[cyan]Groups:[/cyan]  {', '.join(groups)}\n"
        f"[cyan]Duration:[/cyan] {'∞' if duration == 0 else str(duration) + 's'}",
        title="SignalR Connection",
    ))
    console.print("[dim]Press Ctrl+C to stop.[/dim]\n")

    try:
        asyncio.run(connect_hub(hub_url, groups, float(duration), default_message_handler))
    except KeyboardInterrupt:
        console.print("\n[yellow]Interrupted.[/yellow]")


@cli.command()
@click.option("--connections", "-c", default=5, show_default=True, help="Number of concurrent connections.")
@click.option("--duration", "-d", default=15, show_default=True, help="Test duration in seconds.")
@click.option("--group", "-g", type=click.Choice(["metrics", "analytics", "both"]), default="metrics")
def stress(connections: int, duration: int, group: str):
    """Open N simultaneous SignalR connections and count received messages."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    hub_url = dashboard.replace("http://", "ws://").replace("https://", "wss://")
    hub_url = hub_url.rstrip("/") + "/hubs/dashboard"

    groups = ["metrics", "analytics"] if group == "both" else [group]
    stats = StressStats(connections)

    console.print(Panel(
        f"[cyan]Hub URL:[/cyan]     {hub_url}\n"
        f"[cyan]Connections:[/cyan] {connections}\n"
        f"[cyan]Duration:[/cyan]    {duration}s\n"
        f"[cyan]Groups:[/cyan]      {', '.join(groups)}",
        title="SignalR Stress Test",
    ))

    async def run_all():
        tasks = [
            connect_hub(hub_url, groups, float(duration), stats.on_message, connection_id=i)
            for i in range(connections)
        ]
        await asyncio.gather(*tasks, return_exceptions=True)

    try:
        asyncio.run(run_all())
    except KeyboardInterrupt:
        pass

    stats.print_summary()


if __name__ == "__main__":
    cli()
