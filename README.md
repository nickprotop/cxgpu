# cxnvmon

<div align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20|%20Windows-orange.svg)]()

</div>

**An `nvidia-smi`-powered NVIDIA GPU monitor for the terminal, built on [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx).**

<div align="center">

### ⭐ If you find cxnvmon useful, please consider giving it a star! ⭐

It helps others discover the project and motivates continued development.

[![GitHub stars](https://img.shields.io/github/stars/nickprotop/cxnvmon?style=for-the-badge&logo=github&color=yellow)](https://github.com/nickprotop/cxnvmon/stargazers)

</div>

A polished terminal GPU monitor with live utilization, memory, temperature, power and
fan gauges, braille sparkline history, a compute-process table, and full device details —
all without leaving the terminal.

**Monitor your GPU. Right in the terminal.**

![cxnvmon Screenshot](.github/screenshot.png)

## Quick Start

**Option 1: One-line install** (Linux, no .NET required)
```bash
curl -fsSL https://raw.githubusercontent.com/nickprotop/cxnvmon/main/install.sh | bash
cxnvmon
```

**Windows** (PowerShell)
```powershell
irm https://raw.githubusercontent.com/nickprotop/cxnvmon/main/install.ps1 | iex
```

**Option 2: Build from source** (requires .NET 9)
```bash
git clone https://github.com/nickprotop/cxnvmon.git
cd cxnvmon
./build-and-install.sh
cxnvmon
```

> **Requires** the NVIDIA driver and `nvidia-smi` on `PATH`. cxnvmon reads all data from
> `nvidia-smi` and degrades gracefully with a clear message when it isn't available.

## Features

| | |
|---|---|
| 📊 **Overview** | Live utilization, memory, temperature, power and fan gauges with per-metric braille sparkline history |
| 📋 **Processes** | Compute-process table (PID, name, GPU memory) sorted by memory use |
| 🔍 **Details** | Device identity — name, driver, VBIOS, PCIe gen/width, memory, power/temperature limits |
| 📈 **Sparkline Graphs** | Braille-mode time-series graphs with gradient coloring and history tracking |
| 🎛️ **Settings Dialog** | In-app config (F9) — refresh interval and tab visibility, saved to disk |
| 🖱️ **Interactive Status Bar** | Clickable hints that fire actions, plus a live GPU/MEM readout |
| 📐 **Responsive Layout** | Adapts to terminal width — side-by-side or stacked columns |
| 🖥️ **Multi-GPU Aware** | Reads and displays every GPU `nvidia-smi` reports |
| ⚡ **Cross-Platform** | Linux and Windows, wherever `nvidia-smi` runs |

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| F2 | Overview tab |
| F3 | Processes tab |
| F4 | Details tab |
| F9 | Open settings |
| F10 / ESC | Exit |

Status-bar hints are also clickable.

## Building from Source

cxnvmon uses a conditional project reference for [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx):

- **Local development:** If ConsoleEx is cloned as a sibling directory (`../ConsoleEx`), the project reference is used automatically
- **CI/Release builds:** Falls back to the SharpConsoleUI NuGet package

```bash
# Clone both repos as siblings
git clone https://github.com/nickprotop/ConsoleEx.git
git clone https://github.com/nickprotop/cxnvmon.git

# Build with local ConsoleEx
cd cxnvmon
dotnet build cxnvmon/cxnvmon.csproj

# Or build standalone (uses NuGet)
dotnet build cxnvmon/cxnvmon.csproj
```

## Configuration

cxnvmon stores its settings as JSON at the platform config location:

- **Linux:** `~/.config/cxnvmon/config.json` (honours `XDG_CONFIG_HOME`)
- **Windows:** `%APPDATA%\cxnvmon\config.json`

Edit it in-app via the settings dialog (**F9**) — changes are written to disk on save.
A missing or invalid file falls back to defaults, so the app always starts.

## Architecture

```
cxnvmon/
├── cxnvmon/
│   ├── Program.cs                 # Entry point
│   ├── Configuration/             # CxnvmonConfig (JSON load/save)
│   ├── Dashboard/                 # Main window, tabs, status bar, settings dialog
│   ├── Helpers/                   # UI constants, history tracking
│   ├── Stats/                     # GPU stats providers
│   │   ├── IGpuStatsProvider      # Backend-independent interface
│   │   ├── NvidiaSmiGpuStatsProvider  # nvidia-smi reader
│   │   └── GpuStatsFactory        # Backend selection
│   └── Tabs/                      # Tab implementations
│       ├── OverviewTab            # Gauges + sparklines
│       ├── ProcessesTab           # Compute-process table
│       └── DetailsTab             # Device details table
├── publish.sh                     # Release publisher
├── install.sh                     # Linux/macOS installer
└── build-and-install.sh           # Build from source
```

**Key Technologies:** .NET 9, [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx), `nvidia-smi`

## Uninstall

**Linux/macOS:**
```bash
cxnvmon-uninstall.sh
```

**Windows (PowerShell):**
```powershell
& "$env:LOCALAPPDATA\cxnvmon\cxnvmon-uninstall.ps1"
```

## License

MIT License. See [LICENSE](LICENSE) for details.
