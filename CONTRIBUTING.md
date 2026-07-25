# Contributing to cxgpu

Thanks for your interest. Bug reports, hardware reports and pull requests are all welcome.

## Reporting a bug

The most useful report says what hardware you have and what the vendor tool says. Please include:

- OS and version, and the output of `cxgpu --version`
- Your GPU(s), and which backend was serving them (the **Source** row in the Overview spec-sheet)
- What the vendor tool reports directly — `nvidia-smi`, `rocm-smi --showallinfo`, or the relevant
  `/sys/class/drm/cardN/device` files — so a wrong reading can be told apart from a wrong parse

If a value looks wrong on screen, that comparison is the whole bug report.

## Building

cxgpu targets **.NET 10** and builds against [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx).
Clone them as siblings and the project reference resolves automatically; otherwise the NuGet package
is used.

```bash
git clone https://github.com/nickprotop/ConsoleEx.git
git clone https://github.com/nickprotop/cxgpu.git
cd cxgpu
dotnet build
dotnet test cxgpu.Tests/cxgpu.Tests.csproj
```

`--demo[=N]` simulates up to 9 GPUs, so the multi-GPU views can be exercised on any machine.

## Adding hardware support

Each vendor is a self-contained backend, and the UI, alerts and exporter all adapt to what it
declares it can read. See **[Writing a GPU backend](docs/WRITING-A-BACKEND.md)** for the contract, a
worked example, and the checklist.

## The rule that matters most

**Never report a number you did not measure.**

If a sensor is absent or a tool cannot answer, declare the capability `false` and omit the value — do
not substitute `0`. A zero is indistinguishable from a measurement: on screen it reads as a fanless
card spinning at 0 RPM, and in Prometheus it is a fabricated value averaged into a dashboard forever
with nobody noticing. Most review comments on this project trace back to this.

The same applies to the difference between "we cannot tell" and "everything is fine". A backend that
cannot read throttle reasons contributes nothing rather than reporting "not throttling".

## Tests

Pure logic — parsers, thresholds, formatting, aggregation — should be unit-tested. UI code is
verified by running it.

Two habits worth adopting:

**Use real captured output as fixtures.** The `nvidia-smi pmon` tests carry verbatim output from two
driver versions because the column set differs between them; invented fixtures would not have caught
that.

**Check that your test can fail.** Revert the behaviour it guards and confirm it goes red. A test
written alongside an implementation often passes for the wrong reason — one of ours initially passed
against the very bug it was written to prevent, because both fixtures happened to share a column
layout.

## Pull requests

- Keep the change focused; unrelated refactoring makes review harder
- Match the surrounding style — including comment density. Comments here explain *why*, especially
  where something non-obvious was learned the hard way
- Say how you verified it. "Tested on an RTX 3090 and under `--demo`" is worth more than a green CI
  badge
- Note anything you could not verify. An untested platform stated plainly is fine; one implied to
  work is not

## Anything destructive goes through `--demo` first

Process signalling can end someone's job. The demo backend refuses signals and uses PIDs above
`pid_max` precisely so a misfired kill during development cannot reach a real process. Use it.

## License

Contributions are accepted under the [MIT License](LICENSE).
