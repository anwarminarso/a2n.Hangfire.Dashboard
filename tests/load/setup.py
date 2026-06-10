"""
setup.py — One-time setup script for HangfireDashboard testing tools.

Installs Python dependencies and Playwright browsers.

Usage:
    python setup.py
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

TOOLS_DIR = Path(__file__).parent


def run(cmd: list[str], desc: str):
    print(f"\n▶ {desc}")
    print(f"  $ {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=TOOLS_DIR)
    if result.returncode != 0:
        print(f"  ✗ FAILED (exit code {result.returncode})")
        return False
    print(f"  ✓ OK")
    return True


def main():
    print("=" * 60)
    print("  HangfireDashboard Testing Tools — Setup")
    print("=" * 60)

    # 1. Install Python deps
    ok = run(
        [sys.executable, "-m", "pip", "install", "-r", "requirements.txt"],
        "Installing Python dependencies",
    )
    if not ok:
        print("\n⚠ pip install failed. Check requirements.txt and your Python environment.")
        sys.exit(1)

    # 2. Install Playwright browsers
    ok = run(
        [sys.executable, "-m", "playwright", "install", "chromium"],
        "Installing Playwright Chromium browser",
    )
    if not ok:
        print("\n⚠ Playwright install failed. You can still use non-E2E tools.")

    # 3. Copy config if not present
    config_path = TOOLS_DIR / "config.toml"
    example_path = TOOLS_DIR / "config.example.toml"
    if not config_path.exists() and example_path.exists():
        import shutil
        shutil.copy(example_path, config_path)
        print(f"\n▶ Created config.toml from example")
        print(f"  ✓ Edit tools/config.toml to match your environment")
    elif config_path.exists():
        print(f"\n▶ config.toml already exists — skipping copy")

    print("\n" + "=" * 60)
    print("  Setup complete! Next steps:")
    print()
    print("  1. Edit tools/config.toml to match your environment")
    print("  2. Start the SampleApp:  cd samples/SampleApp && dotnet run")
    print("  3. Run a quick check:   python cli.py check-all")
    print()
    print("  Common commands:")
    print("    python cli.py monitor live      # Live terminal dashboard")
    print("    python cli.py e2e smoke         # Playwright smoke test")
    print("    python cli.py jobs seed -n 50   # Seed 50 test jobs")
    print("    python cli.py db stats          # Database statistics")
    print("    python cli.py signalr connect   # Connect to SignalR")
    print("=" * 60)


if __name__ == "__main__":
    main()
