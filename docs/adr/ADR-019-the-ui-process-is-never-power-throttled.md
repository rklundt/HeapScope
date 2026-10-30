# ADR-019 The UI process is never power-throttled (added in sprint 1.5)

- Context: on hybrid CPUs (performance and efficiency cores) Windows schedules the threads of a process whose window is
  not in the foreground on efficiency cores. HeapScope's UI thread then laid out the same grid 2.5 to 5 times slower, so
  the dispatcher budget held or failed depending on where the scheduler put the thread.
- Decision: the UI process opts out of execution speed throttling at startup. The worker keeps its below-normal priority
  and may run on efficiency cores (its throughput work is parallel; the UI's work is latency).
- Consequences: smoke runs measure the code, not the scheduler; users see the same responsiveness with the window in the
  background. Slightly higher power use while the UI is active. An idle UI still runs the dispatcher watchdog's probe
  every 25 ms (a posted no-op, microseconds each), now unthrottled; slowing the probe while the window is idle or in
  the background outside smoke runs is in the backlog (sprint 1.5 review M7).
- Since the Avalonia port (sprint 2.3.6) the opt-out runs on Windows only, where EcoQoS exists; Linux and macOS have no
  equivalent for an application to ask for.
