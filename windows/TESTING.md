# Manual checklist

Run these using the published exe, because trimming can break things only that build shows. Steps marked "two cards" need a second card installed; skip them on a one-card PC.

## Cards and readings

- [ ] The card shows with the right name, and "Display" is on the 5080.
- [ ] Two cards: both show, each with the right name.
- [ ] The tray tooltip names the card, for example `GPU Monitor: 5080 64°C`.
- [ ] Two cards: it names both, for example `GPU Monitor: 5080 64°C, 4070 Ti SUPER 58°C`.
- [ ] The window's values match `nvidia-smi` within 1°C, 1% and 5 W. Under idle load the window itself adds a little power draw.

```
nvidia-smi --query-gpu=pci.bus_id,name,temperature.gpu,utilization.gpu,power.draw --format=csv
```

## Alerts

The heat limit can't go below 50°C, so a card has to be warmed up for this test.

- [ ] Set the heat limit to 50°C and put a game or benchmark on a card. You get one toast, not one per second.
- [ ] If no toast appears, an in-window notice appears when the window is open, and the log says why.
- [ ] Cool the card below 45°C and heat it again. You get one more toast.
- [ ] Two cards: disable the 4070 Ti SUPER in Device Manager. You get "stopped reporting", and its panel turns gray with "No reading since".
- [ ] Two cards: enable it again. You get "is reporting again".

## Diagnostics

- [ ] `GpuMonitor.exe --diagnose` prints a report, and every check reads correctly for this PC.
- [ ] `--diagnose --json -o report.json` writes valid JSON, and your user name and home path are hidden.
- [ ] Create diagnostic report in the tray menu opens the Diagnostics tab with a report. Copy, Save and Open log folder work.
- [ ] `GpuMonitor.exe --crash-test` exits with code 70 and leaves a text file and a minidump in `%LOCALAPPDATA%\GpuMonitor\logs\crash`.
- [ ] Record, then stop. The CSV in `%LOCALAPPDATA%\GpuMonitor\recordings` opens in Excel.

## App behavior

- [ ] Closing the window hides it. The tray icon opens it again.
- [ ] A second launch brings the running window forward, and no second process stays.
- [ ] Exit in the tray menu ends the process, and the tray icon goes away.
- [ ] Start with Windows takes effect after sign-out and sign-in: the app starts hidden in the tray.
- [ ] Signing out with the app running is not held up by it.
- [ ] Sleep the PC and wake it. There's no false "stopped reporting" alert, and the graphs show a gap instead of a line across it.
